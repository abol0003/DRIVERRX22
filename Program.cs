using EasyWaveApp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;


namespace Driver_RX22

{
    class Program
    {
        static async Task Main()
        {
            // List available COM ports 
           /* string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("No COM ports detected.");
                return;
            }

            Console.WriteLine("Available COM ports:");
            for (int i = 0; i < ports.Length; i++)
                Console.WriteLine($"  [{i}] {ports[i]}");

            Console.Write("Enter the port to use: ");
            if (!int.TryParse(Console.ReadLine(), out int idx) || idx < 0 || idx >= ports.Length)
            {
                Console.WriteLine("Invalid.");
                return;
            }*/
            string portName = "/dev/ttyS0";// ports[idx];
            Console.WriteLine($"Using port: {portName}");

            // Instantiate and open the driver
            var driver = new Rx22Driver(portName);
            //////////////////
            driver.FrameReceived += frame =>
                Console.WriteLine("frame received: " + BitConverter.ToString(frame));
            driver.Open();

            var protocol = new Rx22Protocol(driver);

            // Send the EWB_RCV command 
            // 3) Instanciation du service de notifications
            //    On part d’une liste vide pour forcer le pairing interactif
            var service = new NotificationService(protocol);

            Console.WriteLine("Starting notification loop. Press Ctrl+C to exit.");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // 4) Lancement de la boucle asynchrone
            await service.RunAsync(cts.Token);

            Console.WriteLine("Exited.");
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

        public Rx22Driver(string portName)
        {
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
            if (simulate) return;           // <— skip opening in simulation

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
                    Console.Error.WriteLine($"RX22 ReceiveLoop error: {ex}");
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