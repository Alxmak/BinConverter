using System;
using System.Diagnostics;
using System.IO;

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

        public static void Log(string message)
        {
            string line;
            lock (_lock)
            {
                line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            LogWritten?.Invoke(line);
        }

        public static void OpenLogFile()
        {
            if (!File.Exists(LogFilePath))
                File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Лог создан{Environment.NewLine}");

            Process.Start(new ProcessStartInfo
            {
                FileName = LogFilePath,
                UseShellExecute = true
            });
        }

        /// <summary>Полностью очищает файл лога (используется функцией "Очистить кэш" в настройках).</summary>
        public static void ClearLog()
        {
            lock (_lock)
            {
                File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Лог очищен{Environment.NewLine}");
            }
        }
    }
}
