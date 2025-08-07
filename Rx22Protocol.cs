using Iot.Device.Display;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
        QueryState = 0x0A,
        // Not Bidi commands and for TX
        SendCommand = 0x02
    }

    /// <summary>
    /// EasyWave error codes offset 2 of ICP frame
    /// </summary>
    public enum EasyWaveErrorCode : byte
    {
        Success = 0x00,
        ErrCanceled = 0x01,
        ErrOutOfQueue = 0x02,
        ErrInvalidRequest = 0x03,
        ErrSizeMismatch = 0x04,
        ErrInvalidParam = 0x05,
        ErrIncompleteFw = 0x06,
        ErrTimeout = 0x07,
        ErrInvalidSerial = 0x08,
        ErrSuperseded = 0x09,
        ErrIncompatFW = 0x0A,
        ErrSerialFilter = 0x0B,
        ErrFilterOutOfMemory = 0x0C,
        ErrMemory = 0x0D,
        ErrTooLate = 0x0E,
    }

    /// <summary>
    /// Protocol layer for EasyWave RX22 IRP/ICP commands,
    /// </summary>
    public class Rx22Protocol
    {
        private readonly Rx22Driver driver;
        private readonly ILogger<Rx22Protocol> _logger;
        private TaskCompletionSource<byte[]>? pendingRcvTcs; // Tracks pending ReceiveNotification IRP per spec p22

        public Rx22Protocol(Rx22Driver driver, ILogger<Rx22Protocol> logger)
        {
            this.driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private static void EnsureStatusIsOk(ReadOnlySpan<byte> icp, ILogger logger)
        {
            /*if (icp.Length < 3)
                throw new EasyWaveProtocolException(EasyWaveErrorCode.ErrInvalidRequest);*/
            var code = (EasyWaveErrorCode)icp[2];
            if (code != EasyWaveErrorCode.Success)
                logger.LogError("EasyWave error 0x{Code:X2}: {Error}", (byte)code, code);
        }

        /// <summary>
        /// Sends an IRP (commandCode + payload) and waits for the ICP.
        /// Implements supersedure for ReceiveNotification IRPs.
        /// </summary>
        private async Task<byte[]> SendIRP(
            EasyWaveCommand commandCode,
            byte[] payload,
            CancellationToken token = default)
        {
            //_logger.LogDebug("Sending IRP {Command}", commandCode);
            TaskCompletionSource<byte[]> tcs;
            if (commandCode == EasyWaveCommand.ReceiveNotification)
            {
                if (pendingRcvTcs is { Task.IsCompleted: false })
                {
                    pendingRcvTcs.TrySetResult(new byte[] { 0x00, 0x00, (byte)EasyWaveErrorCode.ErrSuperseded });
                }
                // Specially for EWB_RCV
                pendingRcvTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = pendingRcvTcs; // keep memory of status of the irp if waiting or not if => update with new one
            }
            else // For other commands
            {
                tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                // allow to make await for the Handler when the frame arrive and wait for the response via tcs.Task
            }

            byte[] irp = new byte[1 + payload.Length];
            irp[0] = (byte)commandCode;
            Array.Copy(payload, 0, irp, 1, payload.Length);
            Console.WriteLine($"Sending IRP {commandCode} with payload: {BitConverter.ToString(irp)}");
            ushort expectedHandle = 0;
            void Handler(byte[] frameData)
            {
                if (frameData.Length == 2) // IPP
                {
                    expectedHandle = (ushort)((frameData[0] << 8) | frameData[1]);// Combine 2 bytes into 1 unshort of 16 bits
                    return;
                }
                if (frameData.Length >= 3) // ICP
                {
                    ushort handle = (ushort)((frameData[0] << 8) | frameData[1]);
                    if (handle == 0 || handle == expectedHandle)
                        tcs.TrySetResult(frameData);
                }
            }

            driver.FrameReceived += Handler; // We "subscribe" to Handler when Receiving
            try
            {
                await driver.SendAsync(irp, token).ConfigureAwait(false);
                var icp = await tcs.Task.ConfigureAwait(false);
                EnsureStatusIsOk(icp, _logger);
                _logger.LogDebug("Received ICP {Icp}", BitConverter.ToString(icp));
                return icp;
            }
            finally
            {
                driver.FrameReceived -= Handler;// We "unsubscribe" to Handler
            }
        }

        ////// Specific EasyWave commands//////
        /// EWB_RCV: receive Bidi notifications payload
        public class EwbRcvInfo
        {
            public ushort Handle { get; init; }
            public byte Status { get; init; }
            public byte InfoType { get; init; }
            public byte[] DeviceSerial { get; init; } = Array.Empty<byte>();
            public byte[] Additional { get; init; } = Array.Empty<byte>();
        }

        public async Task<EwbRcvInfo> ReceiveNotificationAsync(CancellationToken token = default)
        {
            byte[] icp = await SendIRP(EasyWaveCommand.ReceiveNotification, Array.Empty<byte>(), token).ConfigureAwait(false);

            if (icp.Length == 3)
            {
                _logger.LogInformation("Canceled IRP request with ICP: {Icp}", BitConverter.ToString(icp));
                return new EwbRcvInfo
                {
                    Handle = 0,
                    Status = icp[2],
                    InfoType = 0,
                    DeviceSerial = Array.Empty<byte>(),
                    Additional = Array.Empty<byte>()
                };
            }
            else if (icp.Length < 28)
            {
                throw new InvalidOperationException($"ReceiveNotification failed (len={icp.Length})");
            }

            return new EwbRcvInfo
            {
                Handle = (ushort)((icp[0] << 8) | icp[1]),
                Status = icp[2],
                InfoType = icp[3],
                DeviceSerial = icp.Skip(4).Take(16).ToArray(),
                Additional = icp.Skip(20).Take(8).ToArray()
            };
        }

        /// EWB_GET_FD_SERIAL : read the serial number of a device
        public async Task<byte[]> GetFdSerialAsync(
            ushort index,
            CancellationToken token = default)
        {
            byte[] idx = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)index)); // convert index to big-endian (between 0 and 128)
            byte[] icp = await SendIRP(EasyWaveCommand.GetFdSerial, idx, token).ConfigureAwait(false); // ConfigureAwait(false)

            return icp.Skip(3).Take(16).ToArray();
        }

        /// <summary>
        /// NOT BIDI COMMAND
        /// EW_SEND_CMD : send a command to a device.
        /// </summary>
        public async Task SendCommandAsync(byte[] serial, byte function, CancellationToken ct = default)
        {
            if (serial == null || serial.Length != 16)
                throw new ArgumentException("Serial must be 16 bytes", nameof(serial));

            var payload = new byte[17];
            Array.Copy(serial, 0, payload, 0, 16);
            payload[16] = function;

            await SendIRP(EasyWaveCommand.SendCommand, payload, ct);
        }



        /////////////////////////////////////////////////////////////////////////

        /// EWB_ADD_FILTER: add a filter for a device serial number
        public async Task AddFilterAsync(
            byte[] serial,
            CancellationToken token = default)
        {
            if (serial.Length != 16)
                throw new ArgumentException("Serial must be exactly 16 bytes", nameof(serial));
            await SendIRP(EasyWaveCommand.AddFilter, serial, token).ConfigureAwait(false);
        }

        /// EWB_CLEAR_NFILTER: clear all filters
        public async Task ClearFilterAsync(
            CancellationToken token = default)
        {
            await SendIRP(EasyWaveCommand.ClearFilter, Array.Empty<byte>(), token).ConfigureAwait(false);
        }

        /// EWB_JOIN_DEVICE : Join a device into the network, returns the device serial and type.
        public async Task<(byte[] DeviceSerial, byte DeviceType)> JoinDeviceAsync(
            byte[] gatewaySerial,
            CancellationToken token = default)
        {
            byte[] icp = await SendIRP(EasyWaveCommand.JoinDevice, gatewaySerial, token).ConfigureAwait(false);
            _logger.LogInformation("JoinDevice response: {Icp}", BitConverter.ToString(icp));
            byte status = icp[2];
            byte[] serial = icp.Skip(3).Take(16).ToArray();
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

            _logger.LogInformation("Changing state of device {Serial} to mode={Mode}, state={State}", BitConverter.ToString(joinedSerial), mode, BitConverter.ToString(state));
            await SendIRP(EasyWaveCommand.ChangeState, payload, token).ConfigureAwait(false);
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

            _logger.LogInformation("ControlLearn: device={Serial}, function={Function}, mode={Mode}", BitConverter.ToString(joinedSerial), function, mode);
            await SendIRP(EasyWaveCommand.LearnControl, payload, token).ConfigureAwait(false);
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

            _logger.LogInformation("Removing device {Serial}", BitConverter.ToString(joinedSerial));
            await SendIRP(EasyWaveCommand.RemoveDevice, payload, token).ConfigureAwait(false);
        }

        /// EWB_QUERY_STATE: query mode and state
        public async Task<(byte Mode, byte[] State)> QueryStateAsync(
            byte[] initialSerial,
            byte[] joinedSerial,
            byte mode,
            CancellationToken token = default)
        {
            if (initialSerial.Length != 16) throw new ArgumentException("Initial serial must be 16 bytes", nameof(initialSerial));
            if (joinedSerial.Length != 16) throw new ArgumentException("Joined serial must be 16 bytes", nameof(joinedSerial));

            var payload = new byte[33];
            Array.Copy(initialSerial, 0, payload, 0, 16);
            Array.Copy(joinedSerial, 0, payload, 16, 16);
            payload[32] = mode;

            byte[] icp = await SendIRP(EasyWaveCommand.QueryState, payload, token)
                .ConfigureAwait(false);

            if (icp.Length < 8)
                throw new InvalidOperationException(
                    $"QueryState: response too short (len={icp.Length})");

            byte status = icp[2];
            if (status != 0)
                throw new InvalidOperationException(
                    $"EasyWave error 0x{status:X2} in QueryState");

            byte returnedMode = icp[3];
            byte[] state = icp.Skip(4).Take(4).ToArray();

            _logger.LogInformation("QueryState: mode={Mode}, state={State}", returnedMode, BitConverter.ToString(state));
            return (returnedMode, state);
        }
    }
}
