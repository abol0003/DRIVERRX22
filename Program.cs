using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Driver_RX22;

namespace LoopbackTest
{
    class Program
    {
        static async Task Main()
        {
            // Cancellation token
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Prompt user to select test scenario
            Console.WriteLine("Select test scenario:");
            Console.WriteLine("1) Run existing IRP/ICP sequence");
            Console.WriteLine("2) Run new sync-vs-async test (with interleaved IRP)");
            Console.Write("Enter choice (1 or 2): ");
            var key = Console.ReadKey(intercept: true).KeyChar;
            Console.WriteLine(key);
            bool useNewTest = key == '2';

            // ----- RECEIVER (Stub) setup on COM6 -----
            using var stubPort = new SerialPort("COM6", 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500
            };
            stubPort.DataReceived += StubPort_DataReceived;
            try
            {
                stubPort.Open();
                Console.WriteLine("[Stub-Receiver] Listening on COM6 for IRP frames...");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"[Stub-Receiver] Cannot open COM6: {ex.Message}");
                return;
            }

            // ----- SENDER (Driver) setup on COM5 -----
            var driver = new Rx22Driver("COM5");
            // Filter to only display final ICP (status == 0)
            driver.FrameReceived += frame =>
            {
                if (frame.Length >= 3 && frame[2] == 0)
                {
                    Console.WriteLine("[Driver-Receiver] ICP payload received: " + BitConverter.ToString(frame));
                }
            };
            try
            {
                driver.Open();
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"[Driver] Cannot open COM5: {ex.Message}");
                stubPort.Close();
                return;
            }

            try
            {
                var protocol = new Rx22Protocol(driver);

                if (!useNewTest)
                {
                    // EXISTING IRP/ICP SEQUENCE
                    Console.WriteLine("[Driver] Running existing IRP/ICP sequence...");
                    Console.WriteLine("[Driver] 1. GetFdSerial");
                    byte[] serialGateway = await protocol.GetFdSerialAsync(0, cts.Token);
                    Console.WriteLine("[Driver]    Gateway Serial: " + BitConverter.ToString(serialGateway));

                    Console.WriteLine("[Driver] 2. AddFilter");
                    await protocol.AddFilterAsync(serialGateway, cts.Token);
                    Console.WriteLine("[Driver]    Filter added.");

                    Console.WriteLine("[Driver] 3. JoinDevice");
                    var (serialDevice, deviceType) = await protocol.JoinDeviceAsync(serialGateway, cts.Token);
                    Console.WriteLine($"[Driver]    Device joined: {BitConverter.ToString(serialDevice)}, type=0x{deviceType:X2}");

                    Console.WriteLine("[Driver] 4. QueryState");
                    byte[] queryState = await protocol.QueryStateAsync(serialGateway, serialDevice, 0x00, cts.Token);
                    Console.WriteLine("[Driver]    Queried state: " + BitConverter.ToString(queryState));

                    Console.WriteLine("[Driver] 5. ChangeState");
                    await protocol.ChangeStateAsync(serialGateway, serialDevice, 0x01, new byte[] { 0x00, 0x00, 0x00, 0x01 }, cts.Token);
                    Console.WriteLine("[Driver]    State changed.");

                    Console.WriteLine("[Driver] 6. LearnControl");
                    await protocol.LearnControlAsync(serialGateway, serialDevice, 0x02, 0x00, new byte[] { 0x00, 0x00, 0x00, 0x02 }, cts.Token);
                    Console.WriteLine("[Driver]    Control learned.");

                    Console.WriteLine("[Driver] 7. RemoveDevice");
                    await protocol.RemoveDeviceAsync(serialGateway, serialDevice, cts.Token);
                    Console.WriteLine("[Driver]    Device removed.");

                    Console.WriteLine("[Driver] 8. ClearFilter");
                    await protocol.ClearFilterAsync(cts.Token);
                    Console.WriteLine("[Driver]    Filters cleared.");

                    Console.WriteLine("[Driver] All IRPs were sent successfully.");
                }
                else
                {
                    // NEW SYNC vs ASYNC TEST WITH INTERLEAVED IRP
                    Console.WriteLine("[Driver] Running new sync-vs-async test (interleaved)...");

                    // 1) Test synchronous command
                    Console.WriteLine("[Driver] Sync Test: GetFdSerial");
                    byte[] syncSerial = await protocol.GetFdSerialAsync(0, cts.Token);
                    Console.WriteLine("[Driver]    Sync response: " + BitConverter.ToString(syncSerial));

                    // 2) Test asynchronous notification
                    Console.WriteLine("[Driver] Async Test: ReceiveNotification (expect interleaved frames)");
                    var notification = await protocol.ReceiveNotificationAsync(cts.Token);
                    Console.WriteLine($"[Driver]    Async notification: Handle={notification.Handle}, Type=0x{notification.InfoType:X2}");

                    Console.WriteLine("[Driver] Sync-vs-async test completed.");
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Driver] Error: operation timed out.");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("[Driver] Protocol error: " + ex.Message);
            }
            finally
            {
                driver.Dispose();
                stubPort.Close();
            }
        }

        private static void StubPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var port = (SerialPort)sender;
            int len = port.BytesToRead;
            var buffer = new byte[len];
            port.Read(buffer, 0, len);
            Console.WriteLine("[Stub-Receiver] Raw IRP frame received: " + BitConverter.ToString(buffer));

            var inner = new ArraySegment<byte>(buffer, 1, buffer.Length - 2).ToArray();
            byte[] payload;
            try
            {
                payload = Rx22Driver.Deframe(inner);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Stub-Receiver] Deframe error: {ex.Message}");
                return;
            }

            byte cmd = payload[0];
            Console.WriteLine($"[Stub-Receiver] IRP command code: 0x{cmd:X2}");

            switch (cmd)
            {
                case 0x08:
                    // Send IPP for handle=1
                    byte[] ipp1 = { 0x00, 0x01, 0x01, cmd };
                    port.Write(Rx22Driver.Frame(ipp1), 0, Rx22Driver.Frame(ipp1).Length);
                    Console.WriteLine("[Stub-Receiver] Sent IPP1 for handle=1");

                    // Interleaved bogus IPP for handle=2
                    byte[] ipp2 = { 0x00, 0x02, 0x01, cmd };
                    port.Write(Rx22Driver.Frame(ipp2), 0, Rx22Driver.Frame(ipp2).Length);
                    Console.WriteLine("[Stub-Receiver] Sent interleaved IPP2 for handle=2");

                    // Send final ICP for handle=1
                    byte[] icp = new byte[3 + 1 + 16 + 8];
                    icp[0] = 0x00;
                    icp[1] = 0x01;
                    icp[2] = 0x00;
                    icp[3] = 0x02;
                    for (int i = 0; i < 16; i++) icp[4 + i] = (byte)(0x30 + i);
                    for (int i = 0; i < 8; i++) icp[20 + i] = (byte)(0x40 + i);
                    port.Write(Rx22Driver.Frame(icp), 0, Rx22Driver.Frame(icp).Length);
                    Console.WriteLine("[Stub-Receiver] Sent ICP for handle=1");
                    break;

                default:
                    // Other commands: minimal ICP or JoinDevice
                    byte[] icp2;
                    switch (cmd)
                    {
                        case 0x21: // GetFdSerial
                            icp2 = new byte[3 + 16];
                            icp2[0] = 0x00; icp2[1] = 0x00; icp2[2] = 0x00;
                            for (int i = 0; i < 16; i++) icp2[3 + i] = (byte)(0x10 + i);
                            break;
                        case 0x04: // JoinDevice
                            icp2 = new byte[3 + 16 + 1];
                            icp2[0] = 0x00; icp2[1] = 0x00; icp2[2] = 0x00;

                            // We add a receiver with an other serial number inside the network so need to use a different serial number!
                            for (int i = 0; i < 16; i++) icp2[3 + i] = (byte)(0x20 + i);
                            icp2[3 + 16] = 0x01;
                            break;
                        case 0x07: // AddFilter
                        case 0x06: // ClearFilter
                        case 0x05: // RemoveDevice
                        case 0x09: // ChangeState
                        case 0x0B: // LearnControl
                        case 0x0A: // QueryState
                            icp2 = new byte[3] { 0x00, 0x00, 0x00 };
                            break;
                        default:
                            icp2 = new byte[3] { 0x00, 0x00, 0x00 };
                            break;
                    }
                    port.Write(Rx22Driver.Frame(icp2), 0, Rx22Driver.Frame(icp2).Length);
                    Console.WriteLine("[Stub-Receiver] Sent ICP frame: " + BitConverter.ToString(Rx22Driver.Frame(icp2)));
                    break;
            }
        }
    }
}
