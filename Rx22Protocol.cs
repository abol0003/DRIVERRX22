using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Driver_RX22
{
    /// <summary>
    /// EasyWave command codes for RX22 module,
    /// </summary>
    public enum EasyWaveCommand : byte
    {
        GetFdSerial = 0x21,
        AddFilter = 0x07,
        JoinDevice = 0x04,
        ChangeState = 0x09,
        LearnControl = 0x0B,
        ReceiveNotification = 0x08,
        ClearFilter = 0x06,
        RemoveDevice = 0x05,
        QueryState = 0x0A
    }

    /// <summary>
    /// Protocol layer for EasyWave RX22 IRP/ICP commands,
    /// </summary>
    public class Rx22Protocol
    {
        private readonly Rx22Driver _driver;

        public Rx22Protocol(Rx22Driver driver)
        {
            _driver = driver ?? throw new ArgumentNullException(nameof(driver)); 
        }

        /// <summary>
        /// Sends an IRP (commandCode + payload) and waits for the ICP.
        /// </summary>
        private async Task<byte[]> SendIRP(
            EasyWaveCommand commandCode, // EasyWave command
            byte[] payload,
            CancellationToken token = default)
        {
            byte[] irp = new byte[1+payload.Length]; // construct IRP frame
            irp[0] = (byte)commandCode;
            Array.Copy(payload, 0, irp, 1, payload.Length);

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously); //keep the task alive until we receive the ICP response
            ushort expectedHandle = 0;

            void Handler(byte[] frameData)
            {   // Handle response without expecting command code in ICP
                if (frameData.Length < 3) return;
                // Parse big-endian handle and status
                ushort handle = (ushort)((frameData[0] << 8) | frameData[1]);
                byte status = frameData[2];

                // Synchronous response: handle == 0 and status == 0
                if (handle == 0 && status == 0)
                {
                    tcs.TrySetResult(frameData);
                }
                // Asynchronous pending (IPP): status == 1
                else if (status == 1)
                {
                    expectedHandle = handle;
                }
                // Asynchronous final ICP: handle matches and status == 0
                else if (handle == expectedHandle && status == 0)
                {
                    tcs.TrySetResult(frameData);
                }
            }

            _driver.FrameReceived += Handler;// do it there to capture response before sending the command
            try
            {
                await _driver.SendAsync(irp, token).ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);// wait for handler to receive the icp response
            }
            finally
            {
                _driver.FrameReceived -= Handler;//desabonne from the event to avoid memory leaks
            }
        }

        ////// Specific EasyWave commands (use enum and constants instead of magic numbers) //////

        /// EWB_GET_FD_SERIAL : read the serial number of a device
        public async Task<byte[]> GetFdSerialAsync(
            ushort index,
            CancellationToken token = default)
        {
            byte[] idx = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)index));
            byte[] icp = await SendIRP(EasyWaveCommand.GetFdSerial, idx, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"GetFdSerial failed (status=0x{icp[2]:X2})");

            return icp.Skip(3).Take(16).ToArray();
        }

        /// EWB_ADD_FILTER: add a filter for a device serial number
        public async Task AddFilterAsync(
            byte[] serial,
            CancellationToken token = default)
        {
            if (serial.Length != 16)
                throw new ArgumentException("Serial must be exactly 16 bytes", nameof(serial));
            // Is always structured so no need to make it big endian
            byte[] icp = await SendIRP(EasyWaveCommand.AddFilter, serial, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"AddFilter failed (status=0x{icp[2]:X2})");
        }

        /// EWB_JOIN_DEVICE : Join a device into the network, returns the device serial and type.
        public async Task<(byte[] DeviceSerial, byte DeviceType)> JoinDeviceAsync(
            byte[] gatewaySerial,
            CancellationToken token = default)
        {
            byte[] icp = await SendIRP(EasyWaveCommand.JoinDevice, gatewaySerial, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"JoinDevice failed (status=0x{icp[2]:X2})");

            var serial = icp.Skip(3).Take(16).ToArray();
            byte type = icp[19];
            return (serial, type);
        }

        /// EWB_CHANGE_STATE : Change the state of a device
        public async Task ChangeStateAsync(
            byte[] initialSerial,
            byte[] joinedSerial,
            byte mode,
            byte[] state,
            CancellationToken token = default)
        {
            if (initialSerial.Length != 16) throw new ArgumentException("Initial serial must be 16 bytes", nameof(initialSerial));
            if (joinedSerial.Length != 16) throw new ArgumentException("Joined serial must be 16 bytes", nameof(joinedSerial));
            if (state.Length != 4) throw new ArgumentException("State must be 4 bytes", nameof(state));

            byte[] payload = new byte[37];
            Array.Copy(initialSerial, 0, payload, 0, 16);
            Array.Copy(joinedSerial, 0, payload, 16, 16);
            payload[32] = mode;
            Array.Copy(state, 0, payload, 33, 4);

            byte[] icp = await SendIRP(EasyWaveCommand.ChangeState, payload, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"ChangeState failed (status=0x{icp[2]:X2})");
        }

        /// EWB_TRLRN_CONTROL : Learn or remove a transmitter like button wc or room
        public async Task LearnControlAsync(
            byte[] initialSerial,
            byte[] joinedSerial,
            byte function,
            byte mode,
            byte[] state,
            CancellationToken token = default)
        {
            if (initialSerial.Length != 16) throw new ArgumentException("Initial serial must be 16 bytes", nameof(initialSerial));
            if (joinedSerial.Length != 16) throw new ArgumentException("Joined serial must be 16 bytes", nameof(joinedSerial));
            if (state.Length != 4) throw new ArgumentException("State must be 4 bytes", nameof(state));

            byte[] payload = new byte[38];
            Array.Copy(initialSerial, 0, payload, 0, 16);
            Array.Copy(joinedSerial, 0, payload, 16, 16);
            payload[32] = function;
            payload[33] = mode;
            Array.Copy(state, 0, payload, 34, 4);

            byte[] icp = await SendIRP(EasyWaveCommand.LearnControl, payload, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"LearnControl failed (status=0x{icp[2]:X2})");
        }

        /// EWB_REMOVE_DEVICE: remove a device from the network filter
        public async Task RemoveDeviceAsync(
            byte[] initialSerial,
            byte[] joinedSerial,
            CancellationToken token = default)
        {
            if (initialSerial.Length != 16) throw new ArgumentException("Initial serial must be 16 bytes", nameof(initialSerial));
            if (joinedSerial.Length != 16) throw new ArgumentException("Joined serial must be 16 bytes", nameof(joinedSerial));
            var payload = new byte[32];
            Array.Copy(initialSerial, 0, payload, 0, 16);
            Array.Copy(joinedSerial, 0, payload, 16, 16);
            byte[] icp = await SendIRP(EasyWaveCommand.RemoveDevice, payload, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"RemoveDevice failed (status=0x{icp[2]:X2})");
        }

        /// EWB_CLEAR_NFILTER: clear all filters
        public async Task ClearFilterAsync(
            CancellationToken token = default)
        {
            byte[] icp = await SendIRP(EasyWaveCommand.ClearFilter, Array.Empty<byte>(), token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"ClearFilter failed (status=0x{icp[2]:X2})");
        }

        /// EWB_RCV: receive Bidi notifications payload
        public class EwbRcvInfo
        {
            public ushort Handle { get; init; }            // link btw ipp and icp

            public byte Status { get; init; }            // status of the notification for debug purposes

            public byte InfoType { get; init; }
            public byte[] DeviceSerial { get; init; } = Array.Empty<byte>();
            public byte[] Additional { get; init; } = Array.Empty<byte>();
        }

        public async Task<EwbRcvInfo> ReceiveNotificationAsync(
            CancellationToken token = default)
        {
            byte[] icp = await SendIRP(EasyWaveCommand.ReceiveNotification, Array.Empty<byte>(), token).ConfigureAwait(false);
            if (icp.Length < 28 || icp[2] != 0)
                throw new InvalidOperationException($"ReceiveNotification failed (status=0x{(icp.Length >= 3 ? icp[2] : 0):X2})");

            return new EwbRcvInfo
            {
                Handle = (ushort)((icp[0] << 8) | icp[1]),
                Status = icp[2],
                InfoType = icp[3],
                DeviceSerial = icp.Skip(4).Take(16).ToArray(),
                Additional = icp.Skip(20).Take(8).ToArray()
            };
        }

        /// EWB_QUERY_STATE: query mode and state
        public async Task<byte[]> QueryStateAsync(
            byte[] deviceSerial,
            byte[] joinedSerial,
            byte mode,
            CancellationToken token = default)
        {
            if (deviceSerial.Length != 16) throw new ArgumentException("Device serial must be 16 bytes", nameof(deviceSerial));
            if (joinedSerial.Length != 16) throw new ArgumentException("Joined serial must be 16 bytes", nameof(joinedSerial));
            var payload = new byte[33];
            Array.Copy(deviceSerial, 0, payload, 0, 16);
            Array.Copy(joinedSerial, 0, payload, 16, 16);
            payload[32] = mode;
            byte[] icp = await SendIRP(EasyWaveCommand.QueryState, payload, token).ConfigureAwait(false);
            if (icp[2] != 0)
                throw new InvalidOperationException($"QueryState failed (status=0x{icp[2]:X2})");
            return icp.Skip(3).Take(8).ToArray();
        }
    }
}
