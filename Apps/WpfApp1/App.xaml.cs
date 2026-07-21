using System;
using System.IO;
using System.Windows;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly string CrashLog =
            Path.Combine(AppContext.BaseDirectory, "crash_err.txt");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    File.WriteAllText(CrashLog,
                        "AppDomain.UnhandledException @ " + DateTime.Now + "\n" +
                        (ev.ExceptionObject?.ToString() ?? "<null>") + "\n");
                }
                catch { }
            };

            this.DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    File.AppendAllText(CrashLog,
                        "DispatcherUnhandledException @ " + DateTime.Now + "\n" +
                        (ev.Exception?.ToString() ?? "<null>") + "\n");
                }
                catch { }
                // Let the process terminate so we can see the crash; do not swallow.
            };
        }
    }
}
