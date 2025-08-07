using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Driver_RX22;
using EasyWaveApp;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyWaveGui
{
    public partial class MainWindow : Window
    {
        private readonly Rx22Driver _driver;
        private readonly Rx22Protocol _protocol;
        private readonly NotificationService _notificationService;
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();

            // Création d'un logger nul pour éviter les erreurs de constructeur
            var driverLogger = NullLogger<Rx22Driver>.Instance;
            var protocolLogger = NullLogger<Rx22Protocol>.Instance;
            var notificationLogger = NullLogger<NotificationService>.Instance;

            const string portName = "/dev/ttyS0";
            _driver = new Rx22Driver(portName, driverLogger);
            _driver.FrameReceived += OnFrameReceived;
            _driver.Open();

            _protocol = new Rx22Protocol(_driver, protocolLogger);
            _notificationService = new NotificationService(_protocol, notificationLogger);

            BtnStart.Click += OnStart;
            BtnListen.Click += OnListen;
            BtnStep.Click += OnStep;
            BtnReset.Click += OnReset;
        }

        private void OnFrameReceived(byte[] frame)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ResultText.Text = $"Frame: {BitConverter.ToString(frame)}";
            });
        }

        private async void OnStart(object? sender, RoutedEventArgs e)
        {
            ResultText.Text = "Starting test...";
            try
            {
                var fd = await _protocol.GetFdSerialAsync(0);
                ResultText.Text = $"Gateway serial: {BitConverter.ToString(fd)}";
            }
            catch (Exception ex)
            {
                ResultText.Text = $"Error: {ex.Message}";
            }
        }

        private async void OnListen(object? sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                ResultText.Text = "Notification listening stopped.";
                return;
            }

            ResultText.Text = "Listening for notifications...";
            _cts = new CancellationTokenSource();
            try
            {
                await _notificationService.RunAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                ResultText.Text = "Notification listening cancelled.";
            }
            catch (Exception ex)
            {
                ResultText.Text = $"Error during listening: {ex.Message}";
            }
        }

        private async void OnStep(object? sender, RoutedEventArgs e)
        {
            ResultText.Text = "Waiting for single notification...";
            _cts = new CancellationTokenSource();
            try
            {
                var info = await _protocol.ReceiveNotificationAsync(_cts.Token);
                //ResultText.Text = $"Notification received: type={(int)info.InfoType}";
            }
            catch (OperationCanceledException)
            {
                ResultText.Text = "Notification step cancelled.";
            }
            catch (Exception ex)
            {
                ResultText.Text = $"Error receiving step notification: {ex.Message}";
            }
        }

        private void OnReset(object? sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _driver.Dispose();
            ResultText.Text = "Driver reset.";
        }
    }
}
