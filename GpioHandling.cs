using System;
using System.Threading;
using System.Threading.Tasks;
using System.Device.Gpio;
using Microsoft.Extensions.Logging;

namespace Driver_RX22
{
    /// <summary>
    /// Controls /RESET (active-low, open-drain on RX22) and simple GPIO tests.
    /// </summary>
    public class GpioHandling : IDisposable
    {
        private readonly ILogger<GpioHandling> _logger;
        private readonly GpioController gpio;

        public GpioHandling(ILogger<GpioHandling> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            gpio = new GpioController(PinNumberingScheme.Logical);
        }

        /// <summary>
        /// Assert /RESET by driving LOW for the specified duration, then release to input (Hi-Z).
        /// </summary>
        public async Task PulseResetAsync(int resetPin = 24, int pulseMs = 50, CancellationToken ct = default)
        {
            // Active-low with module's internal pull-up: drive LOW to reset, then release to input.
            EnsurePin(resetPin, PinMode.Output);
            _logger.LogInformation("Asserting /RESET on GPIO {Pin} for {Ms} ms", resetPin, pulseMs);
            gpio.Write(resetPin, PinValue.Low);
            await Task.Delay(pulseMs, ct);

            _logger.LogInformation("Releasing /RESET on GPIO {Pin} (switch to input - high impedance)", resetPin);
            gpio.SetPinMode(resetPin, PinMode.Input);
            await Task.Delay(5, ct); // small settle time
        }

        /// <summary>
        /// Set a GPIO output level (for relay/LED test on board IO lines).
        /// </summary>
        public Task SetPinAsync(int pin, bool high, CancellationToken ct = default)
        {
            EnsurePin(pin, PinMode.Output);
            _logger.LogInformation("GPIO {Pin} <= {Level}", pin, high ? "HIGH" : "LOW");
            gpio.Write(pin, high ? PinValue.High : PinValue.Low);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Pulse a GPIO (useful to blink LEDs or toggle relays).
        /// </summary>
        public async Task PulsePinAsync(int pin, int ms = 200, bool activeHigh = true, CancellationToken ct = default)
        {
            EnsurePin(pin, PinMode.Output);
            _logger.LogInformation("Pulsing GPIO {Pin} for {Ms} ms (activeHigh={ActiveHigh})", pin, ms, activeHigh);
            gpio.Write(pin, activeHigh ? PinValue.High : PinValue.Low);
            await Task.Delay(ms, ct);
            gpio.Write(pin, activeHigh ? PinValue.Low : PinValue.High);
        }

        private void EnsurePin(int pin, PinMode mode)
        {
            if (!gpio.IsPinOpen(pin))
                gpio.OpenPin(pin, mode);
            else
                gpio.SetPinMode(pin, mode);
        }

        public void Dispose() => gpio?.Dispose();
    }
}
