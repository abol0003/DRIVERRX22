using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using EasyWaveApp;

namespace Driver_RX22
{
    /// <summary>
    /// Tx service for EasyWave: get serials and send commands.
    /// </summary>
    public class Tx
    {
        private readonly Rx22Protocol _protocol;
        private readonly ILogger<Tx> _logger;

        public Tx(Rx22Protocol protocol, ILogger<Tx> logger)
        {
            _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static byte BuildFunctionByte(Button button, PushFunction function)
        {
            // Ensure only the lowest 2 bits of button and lowest 6 bits of function are used
            byte btn = (byte)((byte)button & 0x03);
            byte fn6 = (byte)((byte)function & 0x3F);
            return (byte)((fn6 << 2) | btn);
        }

        /// <summary>
        /// Send a single button command (EW_SEND_CMD).
        /// </summary>
        public Task SendCommandAsync(byte[] serial, byte function, CancellationToken ct = default)
        {
            _logger.LogInformation("Send single command to {Serial}, fn={Function}",
                BitConverter.ToString(serial), function);
            return _protocol.SendCommandAsync(serial, function, ct);
        }

        /// <summary>
        /// Send a burst of commands (same code repeated count times).
        /// </summary>
        public async Task SendBurstAsync(byte[] serial, byte function, int count = 3, int delayMs = 100, CancellationToken ct = default)
        {
            for (int i = 0; i < count; i++)
            {
                _logger.LogDebug("Burst {I}/{Count}: fn={Function}", i + 1, count, function);
                await _protocol.SendCommandAsync(serial, function, ct).ConfigureAwait(false);
                if (i < count - 1)
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Completed burst of {Count} commands", count);
        }

        /// <summary>
        /// Start continuous emission of a command until stopped.
        /// </summary>
        public CancellationTokenSource StartContinuous(byte[] serial, byte function, int intervalMs = 100)
        {
            var cts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                _logger.LogInformation("Starting continuous emit fn={Function}", function);
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        await _protocol.SendCommandAsync(serial, function, cts.Token).ConfigureAwait(false);
                        await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                _logger.LogInformation("Stopped continuous emit fn={Function}", function);
            }, CancellationToken.None);
            return cts;
        }

        /// <summary>
        /// Send command continuously for specified duration.
        /// </summary>
        public async Task SendForDurationAsync(byte[] serial, byte function, TimeSpan duration, int intervalMs = 100, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);
            await SendBurstAsync(serial, function, int.MaxValue, intervalMs, cts.Token).ConfigureAwait(false);
        }
    }
}
