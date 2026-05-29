using System;
using System.IO;
using System.Text;

namespace TeslaCamViewer
{
    internal static class CrashLogger
    {
        private static readonly object Gate = new object();

        public static string CrashLogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt");

        public static void Log(string source, Exception exception)
        {
            if (exception == null)
            {
                LogMessage(source, "No exception object was provided.");
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("========================================");
            builder.AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{source}]");
            builder.AppendLine(exception.ToString());
            Append(builder.ToString());
        }

        public static void LogMessage(string source, string message)
        {
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("========================================");
            builder.AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{source}]");
            builder.AppendLine(message ?? string.Empty);
            Append(builder.ToString());
        }

        private static void Append(string text)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(CrashLogPath, text);
                }
            }
            catch
            {
            }
        }
    }
}
