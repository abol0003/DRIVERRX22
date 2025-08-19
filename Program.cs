using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Device.Gpio;
using System.Diagnostics; // Stopwatch
using Driver_RX22;
using EasyWaveApp;

namespace Driver_RX22
{
    class Program
    {
        static async Task Main()
        {
            string portName = "/dev/ttyS0";
            Console.WriteLine($"Using port: {portName}");

            // 1) Configure the DI container
            var services = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Debug);
                })
                // Register Rx22Driver as a singleton, resolving ILogger<Rx22Driver> automatically
                .AddSingleton<Rx22Driver>(sp =>
                    new Rx22Driver(portName,
                        sp.GetRequiredService<ILogger<Rx22Driver>>()))
                // Protocol and services rely on constructor-based DI
                .AddSingleton<Rx22Protocol>()
                .AddSingleton<NotificationService>()
                .AddSingleton<Tx>()
                // GPIO reset/test service
                .AddSingleton<GpioHandling>()
                .BuildServiceProvider();

            // 2) Resolve the main services and use them
            var logger = services.GetRequiredService<ILogger<Program>>();
            var driver = services.GetRequiredService<Rx22Driver>();
            var protocol = services.GetRequiredService<Rx22Protocol>();
            var notifier = services.GetRequiredService<NotificationService>();
            var transmitter = services.GetRequiredService<Tx>();
            var gpio = services.GetRequiredService<GpioHandling>();

            logger.LogInformation("Starting driver on {Port}", portName);
            driver.FrameReceived += frame =>
                logger.LogInformation("Frame received: {Frame}", BitConverter.ToString(frame));
            driver.Open();

            Console.WriteLine();
            Console.WriteLine("Select test:");
            Console.WriteLine("  1) Reception");
            Console.WriteLine("  2) Emission");
            Console.WriteLine("  3) Reset + Relays/LEDs ");
            Console.Write("Choice : ");
            var choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1":
                    //  --- TX ---
                    using (var cts = new CancellationTokenSource())
                    {
                        Console.CancelKeyPress += (_, e) =>
                        {
                            e.Cancel = true;
                            cts.Cancel();
                        };

                        logger.LogInformation("Launching notification loop (press Ctrl+C to stop)...");
                        await notifier.RunAsync(cts.Token);
                        logger.LogInformation("Notification loop stopped.");
                    }
                    break;

                case "2":
                    //  --- TX ---
                    try
                    {
                        ushort index = 1; // index for serial btwn 0 and 127
                        var ct = CancellationToken.None;

                        byte[] serial = await protocol.GetTxSerialAsync(index, ct);
                        if (serial == null || serial.Length == 0)
                        {
                            logger.LogWarning("FD serial is null or empty for index={Index}", index);
                            break;
                        }

                        logger.LogInformation("FD serial[{Len}]: {Serial}",
                            serial.Length, BitConverter.ToString(serial));

                        // Choose the button and function to test
                        byte fn = Tx.BuildFunctionByte(Button.A, PushFunction.Default);
                        logger.LogInformation("TX test start");

                        // Send a short burst 
                        await transmitter.SendBurstAsync(
                            serial,
                            fn,
                            count: 5,       // send 5 frames
                            delayMs: 120,   // 120 ms between frames
                            ct: ct
                        );

                        logger.LogInformation("TX test completed");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "TX test failed");
                    }
                    break;

                case "3":
                    // --- GPIO RESET SEQUENCE (active-low, open-drain on module) ---
                    try
                    {
                        await gpio.PulseResetAsync(24, 50); // GPIO24, 50 ms pulse
                        await Task.Delay(100);              
                        logger.LogInformation("GPIO reset sequence completed on GPIO24.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "GPIO reset failed");
                        break;
                    }

                    // --- Functional readiness probe after reset ---
                    // NOTE: we poll a simple command with a short per-try timeout until the module answers again.
                    try
                    {
                        ushort probeIndex = 1;                         // index used for probing
                        var overallTimeout = TimeSpan.FromSeconds(6);  // total time budget
                        var perTryTimeout = TimeSpan.FromMilliseconds(150);
                        var interval = TimeSpan.FromMilliseconds(200);
                        var sw = Stopwatch.StartNew();
                        Exception? lastErr = null;
                        bool ready = false;

                        while (sw.Elapsed < overallTimeout)
                        {
                            try
                            {
                                using var ctsProbe = new CancellationTokenSource(perTryTimeout);
                                byte[] serialProbe = await protocol.GetTxSerialAsync(probeIndex, ctsProbe.Token);
                                if (serialProbe != null && serialProbe.Length == 16)
                                {
                                    logger.LogInformation("RX22 READY after reset in {Elapsed} ms; serial={Serial}",
                                        sw.ElapsedMilliseconds, BitConverter.ToString(serialProbe));
                                    ready = true;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                // Expected while the module is rebooting: timeouts, IO errors, etc.
                                lastErr = ex;
                            }

                            await Task.Delay(interval);
                        }

                        if (!ready)
                        {
                            logger.LogWarning("RX22 did not respond within {Ms} ms after reset. Last error: {Err}",
                                overallTimeout.TotalMilliseconds, lastErr?.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Functional check after reset failed");
                    }

                    // --- Test relays/LEDs via IO5 & IO6 ---
                    try
                    {
                        await gpio.SetPinAsync(5, true); await Task.Delay(200);
                        await gpio.SetPinAsync(5, false); await Task.Delay(100);
                        await gpio.SetPinAsync(6, true); await Task.Delay(200);
                        await gpio.SetPinAsync(6, false);
                        logger.LogInformation("GPIO quick test done on IO5/IO6.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "GPIO quick test skipped/failed");
                    }
                    break;


                default:
                    Console.WriteLine("No valid choice. Exiting.");
                    break;
            }

            logger.LogInformation("Application stopping");
        }
    }



    /// <summary>
    /// Driver for the EasyWave RX22 module.
    /// </summary>
    public class Rx22Driver : IDisposable
    {
        private const byte SOP = 0x81;
        private const byte EOP = 0x82;
        private const byte ESC = 0x80;

        private readonly ILogger<Rx22Driver> _logger;
        private readonly SerialPort serialPort;
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1); // ensures only one frame is written at a time
        private readonly CancellationTokenSource cts = new CancellationTokenSource(); // Token source for signaling and canceling the background receive loop
        private readonly MemoryStream rxBuffer = new MemoryStream(1024); // buffer for accumulating incoming data

        private readonly bool simulate;

        private Task? receiveLoopTask;

        /// <summary>
        /// Event raised when a full frame is received (payload destuffed).
        /// </summary>
        public event Action<byte[]> FrameReceived = delegate { }; // delegate to avoid error if null

        public Rx22Driver(string portName, ILogger<Rx22Driver> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            simulate = portName == "SIM";
            serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One) // 115200 baud, 8N1 => see spec
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,   // arbitrary chosen and avoiding indefinite blocking
                WriteTimeout = 500, /// PROBLEM
            };
        }

        /// <summary>
        /// Opens the serial port and starts the receive loop.
        /// </summary>
        public void Open()
        {
            if (simulate) return;           // skip opening in simulation

            if (serialPort.IsOpen) return; // Avoid reopening if already open
            serialPort.Open();
            // Start the receive loop in a background task
            receiveLoopTask = Task.Run(() => ReceiveLoopAsync(cts.Token), CancellationToken.None);
        }

        /// <summary>
        /// Sends a payload
        /// </summary>
        public async Task SendAsync(byte[] payload, CancellationToken cancellation = default)
        {
            byte[] framed = Frame(payload); // add SOP, EOP and byte stuffing
            if (simulate)
            {
                return;
            }
            await writeLock.WaitAsync(cancellation).ConfigureAwait(false); // wait to get token to enable acces to port ( avoid 2 simultaneous writes )
            try
            {
                // Use async write to respect cancellation
                await serialPort.BaseStream.WriteAsync(framed, 0, framed.Length, cancellation).ConfigureAwait(false);
            }
            finally
            {
                // release the semaphore so other calls can proceed
                writeLock.Release();
            }
        }

        /// <summary>
        /// Builds a frame: SOP + byte stuffing + payload + EOP.
        /// </summary>
        public static byte[] Frame(ReadOnlySpan<byte> payload)
        {
            byte[] buffer = new byte[2 + payload.Length * 2]; // worst case size : SOP+EOP plus max stuffing
            int index = 0;
            buffer[index++] = SOP;
            foreach (byte b in payload)
            {
                if (b == SOP || b == EOP || b == ESC)
                {
                    buffer[index++] = ESC;
                    buffer[index++] = (byte)(b - ESC);
                }
                else
                {
                    buffer[index++] = b;
                }
            }
            buffer[index++] = EOP;
            // Return only used portion
            Array.Resize(ref buffer, index);
            return buffer;
        }

        /// <summary>
        /// Continuously reads from the serial port and extracts frames.
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            byte[] buffer = new byte[1024]; // arbitrary chosen buffer size for reading data
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await serialPort.BaseStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (bytesRead <= 0) continue;

                    // Append to rxBuffer
                    rxBuffer.Write(buffer, 0, bytesRead);
                    ProcessPayload();
                }
                catch (OperationCanceledException)
                {

                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RX22 ReceiveLoop error");
                    break; // Exit the loop on any error
                }
                // If to many errors maybe add a close and open port to remake the link
            }
        }

        /// <summary>
        /// extract payload from received data in rxBuffer.
        /// </summary>
        public void ProcessPayload()
        {
            var data = rxBuffer.ToArray();
            int offset = 0;

            while (true)
            {
                // Find SOP and EOP in the data
                int sopIndex = Array.IndexOf(data, SOP, offset);
                if (sopIndex < 0) break;
                int eopIndex = Array.IndexOf(data, EOP, sopIndex + 1);
                if (eopIndex < 0) break;

                // Extract payload
                int length = eopIndex - sopIndex - 1;
                var frame = new ReadOnlySpan<byte>(data, sopIndex + 1, length);
                byte[] payload = Deframe(frame); // inverse byte stuffing
                FrameReceived(payload);

                offset = eopIndex + 1;
            }

            //avoid data loss by keeping remaining data in the buffer
            if (offset > 0)
            {
                var remaining = new ReadOnlySpan<byte>(data, offset, data.Length - offset).ToArray();
                rxBuffer.SetLength(0);
                rxBuffer.Write(remaining, 0, remaining.Length);
            }
        }

        /// <summary>
        /// Reconstructs the payload by reversing byte stuffing.
        /// </summary>
        public static byte[] Deframe(ReadOnlySpan<byte> frame)
        {
            var list = new List<byte>(frame.Length);
            for (int i = 0; i < frame.Length; i++)
            {
                if (frame[i] == ESC && i + 1 < frame.Length) // reconstruct the original byte from the 2 bytes send into one 0x80+0x01=0x81
                {
                    byte stuffed = frame[++i];
                    // Validate stuffed value
                    if (stuffed > 2)
                        throw new InvalidDataException("Invalid ESC sequence in frame.");
                    list.Add((byte)(ESC + stuffed));
                }
                else
                {
                    list.Add(frame[i]);
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Disposes the driver, stopping the receive loop and closing the port.
        /// </summary>
        public void Dispose()
        {
            cts.Cancel();
            try { receiveLoopTask?.Wait(); } catch { }

            if (serialPort.IsOpen)
                serialPort.Close();

            serialPort.Dispose();
            writeLock.Dispose();
            rxBuffer.Dispose();
            cts.Dispose();
            GC.SuppressFinalize(this);
        }

        ///////////////////////////////////     ONLY FOR SIMULATION       ////////////////////////////////////////////
        /// <summary>
        /// For simulation: inject raw bytes into the internal buffer and process them.
        /// </summary>
        public void FeedRawData(byte[] chunk)
        {
            rxBuffer.Write(chunk, 0, chunk.Length);
            ProcessPayload();
        }

        /// <summary>
        /// For simulation: get the current leftover bytes still in the internal buffer.
        /// </summary>
        public byte[] RemainingBuffer => rxBuffer.ToArray();

    }
}
