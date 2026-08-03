using System;
using System.Diagnostics;
using System.IO;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    public static class AppLogger
    {
        public static string LogFilePath { get; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TweakFirmware.log.txt");

        private static readonly object _lock = new();

        /// <summary>
        /// Срабатывает при каждой новой строке лога — на неё подписывается окно
        /// логов в интерфейсе, чтобы показывать записи в реальном времени.
        /// Может вызываться с фонового потока (Log вызывается из Task.Run) —
        /// подписчики обязаны сами перейти в UI-поток при необходимости.
        /// </summary>
        public static event Action<string>? LogWritten;

        private static string FormatTimestamp() =>
            $"{Strings.Get("Log_TimestampLabel")}: {DateTime.Now:MM.dd.yyyy, HH:mm:ss}:";

        public static void Log(string message)
        {
            string line;
            lock (_lock)
            {
                line = $"{FormatTimestamp()} {message}";
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            LogWritten?.Invoke(line);
        }

        public static void OpenLogFile()
        {
            if (!File.Exists(LogFilePath))
                File.WriteAllText(LogFilePath, $"{FormatTimestamp()} {Strings.Get("Log_Created")}{Environment.NewLine}");

            Process.Start(new ProcessStartInfo
            {
                FileName = LogFilePath,
                UseShellExecute = true
            });
        }

        /// <summary>Полностью очищает файл лога (используется функцией "Очистить кеш" в настройках).</summary>
        public static void ClearLog()
        {
            lock (_lock)
            {
                File.WriteAllText(LogFilePath, $"{FormatTimestamp()} {Strings.Get("Log_Cleared")}{Environment.NewLine}");
            }
        }
    }
}
