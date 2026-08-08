using System;
using System.Diagnostics;
using System.IO;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Журнал работы: строка в файл и то же самое подписчикам для показа в окне.
    ///
    /// Главное свойство — <see cref="Log"/> не бросает исключений. Он вызывается из циклов
    /// чтения и записи внутри нарезки и сборки, из уборки за сорванной операцией и при
    /// самом запуске программы. Пока защиты не было, любая неудача записи (папка только
    /// для чтения, файл занят антивирусом, кончилось место) убивала саму операцию — а на
    /// заполненном диске это происходило прямо внутри обработчика ошибки «кончилось
    /// место», из-за чего уборка прерывалась и недописанные файлы оставались на диске.
    /// </summary>
    public static class AppLogger
    {
        /// <summary>
        /// Файл журнала лежит там же, где остальные настройки программы (%AppData%),
        /// а не рядом с exe: папка установки может быть только для чтения, а портативную
        /// версию распаковывают куда угодно — вплоть до сетевого диска или флешки
        /// с защитой от записи.
        /// </summary>
        public static string LogFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tweak Firmware", "TweakFirmware.log.txt");

        private static readonly object _lock = new();

        /// <summary>
        /// Срабатывает при каждой новой строке лога — на неё подписано окно журнала,
        /// чтобы показывать записи в реальном времени. Может вызываться с фонового
        /// потока: подписчики обязаны сами перейти в поток интерфейса.
        /// </summary>
        public static event Action<string>? LogWritten;

        /// <summary>Дата в привычном порядке «день.месяц.год» — было "MM.dd.yyyy",
        /// то есть 08.01.2026 означало 1 августа, а читалось как 8 января.</summary>
        private static string FormatTimestamp() =>
            $"{Strings.Get("Log_TimestampLabel")}: {DateTime.Now:dd.MM.yyyy, HH:mm:ss}:";

        public static void Log(string message)
        {
            string line = $"{FormatTimestamp()} {message}";

            // Строка уходит подписчикам всегда: даже если на диск записать не удалось,
            // в окне журнала человек ход операции видеть должен.
            TryAppend(line);
            LogWritten?.Invoke(line);
        }

        public static void OpenLogFile()
        {
            if (!EnsureFileExists()) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = LogFilePath, UseShellExecute = true });
            }
            catch
            {
                // Не открылось — сообщать об этом окном поверх журнала незачем.
            }
        }

        /// <summary>Полностью очищает файл журнала. Возвращает false, если не удалось.</summary>
        public static bool ClearLog() => TryWrite($"{FormatTimestamp()} {Strings.Get("Log_Cleared")}");

        /// <summary>Создаёт файл журнала, если его ещё нет. Возвращает false, если не удалось.</summary>
        public static bool EnsureFileExists()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(LogFilePath)) return true;
                }
                catch
                {
                    return false;
                }
            }

            return TryWrite($"{FormatTimestamp()} {Strings.Get("Log_Created")}");
        }

        private static void TryAppend(string line)
        {
            lock (_lock)
            {
                try
                {
                    EnsureDirectory();
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
                catch
                {
                    // Молча: см. пояснение к классу. Сообщить о неудаче записи в журнал
                    // можно было бы только… записью в журнал.
                }
            }
        }

        private static bool TryWrite(string line)
        {
            lock (_lock)
            {
                try
                {
                    EnsureDirectory();
                    File.WriteAllText(LogFilePath, line + Environment.NewLine);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void EnsureDirectory()
        {
            string? dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }
}
