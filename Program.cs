using Avalonia;
using Avalonia.LinuxFramebuffer;
using System;

namespace EasyWaveGui
{
    class Program
    {
        [STAThread]
        public static void Main(string[] args) =>
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                      .UsePlatformDetect()
                      .LogToTrace();
        }
    }
}
