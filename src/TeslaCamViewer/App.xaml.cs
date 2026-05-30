using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;
using Velopack;

namespace TeslaCamViewer
{
    public partial class App : Application
    {
        private Window m_window;

        public App()
        {
            VelopackApp.Build().Run();
            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            CrashLogger.Log("WinUI unhandled exception", e.Exception);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            CrashLogger.Log("AppDomain unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal exception"));
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            CrashLogger.Log("Unobserved task exception", e.Exception);
            e.SetObserved();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow(GetStartupSourcePath());
            m_window.Activate();
        }

        private static string GetStartupSourcePath()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--source", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                const string sourcePrefix = "--source=";
                if (arg.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(sourcePrefix.Length).Trim('"');
                }
            }

            return null;
        }
    }
}
