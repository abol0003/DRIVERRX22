using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Device.Gpio;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Driver_RX22;     
using EasyWaveApp;     

using RxButton = EasyWaveApp.Button;
using RxPushFunction = EasyWaveApp.PushFunction;

namespace InterfaceUNO;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<string> _lines = new();

    private readonly string[] portCandidates = { "/dev/ttyS0", "/dev/serial0", "/dev/ttyAMA0", "/dev/ttyUSB0", "/dev/ttyACM0" };
    private string _port = string.Empty;

    // Pile Driver
    private readonly Rx22Driver driver;
    private readonly Rx22Protocol protocol;
    private readonly NotificationService notification;
    private readonly Tx tx;
    private readonly GpioHandling gpio;

    // RunAsync control
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public MainPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _lines;

        // Port Choice
        _port = SelectExistingPort(portCandidates);
        Append($"[Port] Selected: '{_port}'");
 
        var uiLogDriver = new UiLogger<Rx22Driver>(Append);
        var uiLogProtocol = new UiLogger<Rx22Protocol>(Append);
        var uiLogNotif = new UiLogger<NotificationService>(Append);

        driver = new Rx22Driver(_port, uiLogDriver);          
        protocol = new Rx22Protocol(driver, uiLogProtocol);    
        notification = new NotificationService(protocol, uiLogNotif);
        tx = new Tx(protocol, NullLogger<Tx>.Instance);
        gpio = new GpioHandling(NullLogger<GpioHandling>.Instance);

        driver.FrameReceived += OnFrameReceived;

        this.Loaded += async (_, __) =>
        {
            await PreflightAsync(_port); // check non-bloquants
            try
            {
                driver.Open();          // run receiver
                Append("[Driver] Open OK.");
            }
            catch (Exception ex)
            {
                Append($"[Driver][ERROR] Open failed on '{_port}': {ex.Message}");
            }
        };

        Append("UI ready.");
    }

    private void OnFrameReceived(byte[] frame)
    {
        if (frame.Length == 2)
        {
            Append($"[IPP] {Hex(frame)}");
            return;
        }

        // Protocol
        if (frame.Length >= 28)
        {
            ushort handle = (ushort)((frame[0] << 8) | frame[1]);
            byte status = frame[2];
            byte infoType = frame[3];
            var serial = new ReadOnlySpan<byte>(frame, 4, 16);
            var add = new ReadOnlySpan<byte>(frame, 20, 8);

            //Append($"[ICP] handle=0x{handle:X4} status=0x{status:X2} type=0x{infoType:X2} serial={Hex(serial)} add={Hex(add)}");
        }
        else
        {
            Append($"[ICP] {Hex(frame)}");
        }
    }

    // === Boutons UI ===

    // Start/stop the bidi notification loop (ReceiveNotification) 
    private void OnRunAsyncClicked(object sender, RoutedEventArgs e)
    {
        if (_cts?.IsCancellationRequested == false)
        {
            _cts.Cancel();
            Append("[RunAsync] Stop requested.");
            return;
        }

        _cts = new CancellationTokenSource();
        Append("[RunAsync] Starting NotificationService.RunAsync…");
        _runTask = notification.RunAsync(_cts.Token);
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        Append("[Send] Checking link (GetFdSerial)…");
        try
        {
            
            var serial = await protocol.GetTxSerialAsync(1, CancellationToken.None);
            Append("[Send] Link OK, FD[1]=" + BitConverter.ToString(serial));
            byte fn = Tx.BuildFunctionByte(RxButton.A, RxPushFunction.Default);
            Append($"[Send] Burst: fn=0x{fn:X2}, count=5, delay=120ms…");
            await tx.SendBurstAsync(serial, fn, 5, 120, CancellationToken.None);
            Append("[Send] Burst done.");
        }
        catch (Exception ex)
        {
            Append($"[Send][ERROR] {ex.Message}");
        }
    }

    // Reset 
    private async void OnGpioResetClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Append("[GPIO] Pulse RESET on GPIO24…");
            await gpio.PulseResetAsync(24, 50, CancellationToken.None);
            Append("[GPIO] Reset done.");
        }
        catch (Exception ex)
        {
            Append($"[GPIO][ERROR] {ex.Message}");
        }
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        Append("[Exit] Closing app…");
        try { Application.Current.Exit(); }
        catch { Environment.Exit(0); }
    }


    private async Task PreflightAsync(string portName)
    {
        // Enumerate ports
        try
        {
            var names = SerialPort.GetPortNames();
            Append("[Check] Serial ports: " + (names.Length == 0 ? "(none)" : string.Join(", ", names)));
            Append($"[Check] {portName} exists: {File.Exists(portName)}");
        }
        catch (Exception ex)
        {
            Append($"[Check][WARN] SerialPort.GetPortNames: {ex.Message}");
        }

        // Acces GPIO 
        try
        {
            using var ctrl = new GpioController(PinNumberingScheme.Logical);
            if (!ctrl.IsPinOpen(24))
                ctrl.OpenPin(24, PinMode.Input);
            Append("[Check] GPIO access OK.");
        }
        catch (Exception ex)
        {
        Append($"[Check][GPIO] {ex.Message} (add the user to the gpio/dialout groups if needed)");
        }

        await Task.Delay(20);
    }

    // === Utilitaires ===
    private static string SelectExistingPort(string[] candidates)
    {
        foreach (var p in candidates)
            if (File.Exists(p)) return p;
        // If no port is found, return the first candidate
        return candidates.Length > 0 ? candidates[0] : "/dev/ttyS0";
    }

    private static string Hex(ReadOnlySpan<byte> s) => BitConverter.ToString(s.ToArray());

    private void Append(string line)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            _lines.Add(line);
            StatusText.Text = line;
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]); // ^1 is the last item
        });
    }

    //  ILogger 
    private sealed class UiLogger<T> : ILogger<T>
    {
        private readonly Action<string> _sink;
        public UiLogger(Action<string> sink) => _sink = sink;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                                Func<TState, Exception?, string> fmt)
        {
            var msg = fmt(state, ex);
            if (ex != null) msg += $" EX: {ex.Message}";
            _sink($"[{level}] {msg}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
