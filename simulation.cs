using Driver_RX22;
using System;
using System.IO;
using System.Linq;

namespace Driver_RX22
{
    /// <summary>
    /// Simulation  Frame/Deframe, Buffer Processing, and IRP/ICP Protocol
    /// </summary>
    class Simulation
    {
        static void Main(string[] args)
        {
            // ====== Stuffing/Destuffing Simulation ======
            byte[][] testPayloads = new byte[][]
            {
                new byte[] { 0x01, 0x02, 0x03 },    // simple
                new byte[] { 0x81, 0x82, 0x80 },    // all special bytes
                new byte[] { 0x10, 0x81, 0x20, 0x82, 0x30, 0x80, 0x40 }, // mixed
                new byte[0],                        // empty
                new byte[] { 0xFF, 0x00, 0x81, 0x42, 0x80, 0x01, 0x82 } // random
            };
            Console.WriteLine("\n=== Stuffing Destuffing Simulation ===");
            foreach (var payload in testPayloads)
            {
                Console.WriteLine("--- New Test ---");
                Console.WriteLine("Original payload: " + BitConverter.ToString(payload));

                byte[] framed = Rx22Driver.Frame(payload);
                Console.WriteLine("Framed data:     " + BitConverter.ToString(framed));

                var inner = new ArraySegment<byte>(framed, 1, framed.Length - 2).ToArray();
                byte[] deframed = Rx22Driver.Deframe(inner);
                Console.WriteLine("Deframed data:   " + BitConverter.ToString(deframed));

                if (payload.Length != deframed.Length)
                    throw new Exception($"Length mismatch: expected {payload.Length}, got {deframed.Length}");
                for (int i = 0; i < payload.Length; i++)
                    if (payload[i] != deframed[i])
                        throw new Exception($"Data mismatch at index {i}: expected {payload[i]:X2}, got {deframed[i]:X2}");
            }

            // ====== Buffer Processing Simulation ======
            Console.WriteLine("\n=== Buffer Processing Simulation ===");
            var frame1 = Rx22Driver.Frame(new byte[] { 0xAA, 0xBB });
            var frame2 = Rx22Driver.Frame(new byte[] { 0xCC });
            var frame3 = Rx22Driver.Frame(new byte[] { 0xDD, 0xEE });
            var stream = frame1.Concat(frame2).Concat(frame3).ToArray();

            var bufferDriver = new Rx22Driver("SIM");
            bufferDriver.FrameReceived += data =>
                Console.WriteLine("Received frame: " + BitConverter.ToString(data));

            int chunkSize = 5;
            for (int i = 0; i < stream.Length; i += chunkSize)
            {
                int len = Math.Min(chunkSize, stream.Length - i);
                var chunk = new byte[len];
                Array.Copy(stream, i, chunk, 0, len);
                Console.WriteLine($"\n--- Received chunk ({len} bytes): {BitConverter.ToString(chunk)}");
                bufferDriver.FeedRawData(chunk);
                Console.WriteLine("Buffer after processing: " + BitConverter.ToString(bufferDriver.RemainingBuffer));
            }

            // ====== Protocol IRP/ICP Simulation ======
            Console.WriteLine("\n=== Protocol IRP/ICP Simulation ===");
            var simDriver = new Rx22Driver("SIM");
            var protocol = new Rx22Protocol(simDriver);

            // Build fake ICP responses (prepend command code)
            byte[] icpGetSerial = new byte[19];
            icpGetSerial[0] = 0x21;           // command code EWB_GET_FD_SERIAL
            icpGetSerial[1] = 0x00;           // filler
            icpGetSerial[2] = 0x00;           // status = 0
            for (int i = 0; i < 16; i++) icpGetSerial[3 + i] = (byte)(i + 1);  // serial number 1-16 sim

            byte[] icpClear = new byte[] { 0x06, 0x00, 0x00 };  // command code, filler, status
            byte[] icpAdd = new byte[] { 0x07, 0x00, 0x00 };  // command code, filler, status

            byte[] icpJoin = new byte[20];
            icpJoin[0] = 0x04;            // command code EWB_JOIN_DEVICE
            icpJoin[1] = 0x00;            // filler
            icpJoin[2] = 0x00;            // status = 0
            for (int i = 0; i < 16; i++) icpJoin[3 + i] = (byte)(0xA0 + i);
            icpJoin[19] = 0x03;           // device type

            // 1) GetFdSerialAsync
            Console.WriteLine("--- Simulate GetFdSerialAsync(0) ---");
            var serialTask = protocol.GetFdSerialAsync(0);
            simDriver.FeedRawData(Rx22Driver.Frame(icpGetSerial));
            byte[] serialResult = serialTask.GetAwaiter().GetResult();
            Console.WriteLine("GetFdSerialAsync returned: " + BitConverter.ToString(serialResult));

            // 2) ClearFilterAsync
            Console.WriteLine("--- Simulate ClearFilterAsync() ---");
            var clearTask = protocol.ClearFilterAsync();
            simDriver.FeedRawData(Rx22Driver.Frame(icpClear));
            clearTask.GetAwaiter().GetResult();
            Console.WriteLine("ClearFilterAsync completed successfully.");

            // 3) AddFilterAsync
            Console.WriteLine("--- Simulate AddFilterAsync(serial) ---");
            var addTask = protocol.AddFilterAsync(serialResult);
            simDriver.FeedRawData(Rx22Driver.Frame(icpAdd));
            addTask.GetAwaiter().GetResult();
            Console.WriteLine("AddFilterAsync completed successfully.");

/*            // 4) JoinDeviceAsync
            Console.WriteLine("--- Simulate JoinDeviceAsync(serial) ---");
            // Start JoinDeviceAsync (internally subscribes to AddFilter)
            var joinTask = protocol.JoinDeviceAsync(serialResult);
            // Inject internal AddFilterAsync response
            simDriver.FeedRawData(Rx22Driver.Frame(icpAdd));
            // Inject JoinDevice response
            simDriver.FeedRawData(Rx22Driver.Frame(icpJoin));
            var joinResult = joinTask.GetAwaiter().GetResult();
            byte[] joinedSerial = joinResult.DeviceSerial;
            byte devType = joinResult.DeviceType;
            Console.WriteLine($"JoinDeviceAsync returned serial {BitConverter.ToString(joinedSerial)} and type {devType}");
*/
/*            // Cleanup
            simDriver.Dispose();
            bufferDriver.Dispose();
            simDriver.Dispose();
            bufferDriver.Dispose();
            simDriver.Dispose();
            bufferDriver.Dispose();*/
        }
    }
}
