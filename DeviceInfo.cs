using System;
using System.Threading;
using System.Threading.Tasks;
using Driver_RX22;

namespace EasyWaveApp
{
    /// <summary>
    /// High-level event for a device notification.
    /// </summary>
    public class DeviceInfo
    {
        public InfoType InfoType { get; init; }           // What kind of event arrived
        public required byte[] Serial { get; init; }
        public required byte[] Additional { get; init; }
        public string Id => BitConverter.ToString(Serial);

        public static DeviceInfo FromFrame(Rx22Protocol.EwbRcvInfo frame)
        {
            if (frame.Status != 0)
                throw new InvalidOperationException($"Frame error status=0x{frame.Status:X2}");

            return new DeviceInfo
            {
                InfoType = (InfoType)frame.InfoType,
                Serial = frame.DeviceSerial,
                Additional = frame.Additional
            };
        }
    }

    /// <summary>
    /// Service that listens for EasyWave notifications and dispatches actions.
    /// </summary>
    public class NotificationService
    {
        private readonly Rx22Protocol _protocol;

        public NotificationService(Rx22Protocol protocol)
        {
            _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Rx22Protocol.EwbRcvInfo frame;
                try
                {
                    frame = await _protocol.ReceiveNotificationAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Receive error: {ex.Message}");
                    continue;
                }

                var info = DeviceInfo.FromFrame(frame);
                Dispatch(info);
            }
        }

        private void Dispatch(DeviceInfo info)
        {
            var identifier = info.Id;
            switch (info.InfoType)
            {
                case InfoType.PushAndHold:
                    {
                        byte b = info.Additional[0];
                        Button btn = (Button)(b & 0x03);
                        PushFunction fn = (PushFunction)((b >> 2) & 0x3F);
                        if (fn == PushFunction.LowBattery)
                        {
                            Console.WriteLine($"[{identifier}] Low battery alert");
                        }
                        else
                        {
                            Console.WriteLine($"[{identifier}] Button {btn} pressed, function {fn}");
                        }
                        break;
                    }
                case InfoType.Release:
                    {
                        Button btn = (Button)(info.Additional[0] & 0x03);
                        Console.WriteLine($"[{identifier}] Button {btn} released");
                        break;
                    }
                case InfoType.Sensor:
                    Console.WriteLine($"[{identifier}] Sensor data: {BitConverter.ToString(info.Additional)}");
                    break;
                case InfoType.StateChange:
                    Console.WriteLine($"[{identifier}] State change, mode={info.Additional[0]}, state={BitConverter.ToString(info.Additional, 1, 4)}");
                    break;
                case InfoType.LearnStart:
                case InfoType.LearnComplete:
                case InfoType.LearnFail:
                    Console.WriteLine($"[{identifier}] Learn event: {info.InfoType}");
                    break;
                default:
                    Console.WriteLine($"[{identifier}] Unhandled InfoType: {info.InfoType}");
                    break;
            }
        }
    }

    public enum InfoType : byte
    {
        Release = 0x00,
        PushAndHold = 0x01,
        Sensor = 0x02,
        StateChange = 0x03,
        LearnStart = 0x40,
        LearnComplete = 0x41,
        LearnFail = 0x42
    }

    public enum Button : byte
    {
        A = 0,
        B = 1,
        C = 2,
        D = 3
    }

    public enum PushFunction : byte
    {
        Default = 0,
        RemoteLearnDelete = 1,
        RemoteLearnAdd = 2,
        RemoteLearnReset = 3,
        RemoteLearnSetTimer = 4,
        EmulatedHold = 5,
        EmulatedRelease = 6,
        LowBattery = 0x20
    }
}
