using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        /// <summary>
        /// Предел размера файла журнала. Файл только дописывался — по строке на каждую
        /// созданную часть, за все запуски программы, — и рос без границы. При достижении
        /// предела старые записи отбрасываются, свежие сохраняются: журнал нужен, чтобы
        /// разобраться в том, что произошло сейчас, а не полгода назад.
        /// </summary>
        private const long MaxFileBytes = 1024 * 1024;

        /// <summary>Сколько последних строк остаётся после обрезки.</summary>
        private const int KeepLinesOnTrim = 2000;

        private static readonly object _lock = new();

        /// <summary>
        /// Размер файла, чтобы не спрашивать его у файловой системы на каждой строке:
        /// за большую операцию строк тысячи. -1 — ещё не смотрели.
        /// </summary>
        private static long _knownSize = -1;

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

                    string text = line + Environment.NewLine;
                    File.AppendAllText(LogFilePath, text);

                    if (_knownSize < 0) _knownSize = new FileInfo(LogFilePath).Length;
                    else _knownSize += Encoding.UTF8.GetByteCount(text);

                    if (_knownSize > MaxFileBytes) TrimOldestEntries();
                }
                catch
                {
                    // Молча: см. пояснение к классу. Сообщить о неудаче записи в журнал
                    // можно было бы только… записью в журнал.
                    _knownSize = -1; // размер больше не знаем — перечитаем при следующей записи
                }
            }
        }

        /// <summary>
        /// Оставляет последние <see cref="KeepLinesOnTrim"/> строк. Вызывается изнутри
        /// уже взятой блокировки. Читать файл целиком здесь не страшно: это происходит
        /// один раз на мегабайт записей, а не на каждую строку.
        /// </summary>
        private static void TrimOldestEntries()
        {
            try
            {
                string[] lines = File.ReadAllLines(LogFilePath);
                if (lines.Length <= KeepLinesOnTrim)
                {
                    // Строк мало, а байтов много: файл раздут длинными строками, обрезать
                    // по строкам нечего. Пересчитываем размер и оставляем как есть, иначе
                    // обрезка вызывалась бы на каждой следующей записи.
                    _knownSize = new FileInfo(LogFilePath).Length;
                    return;
                }

                var kept = new string[KeepLinesOnTrim + 1];
                kept[0] = Strings.Format("Log_TrimmedOlderEntries", lines.Length - KeepLinesOnTrim);
                Array.Copy(lines, lines.Length - KeepLinesOnTrim, kept, 1, KeepLinesOnTrim);

                File.WriteAllLines(LogFilePath, kept);
                _knownSize = new FileInfo(LogFilePath).Length;
            }
            catch
            {
                _knownSize = -1;
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
                    _knownSize = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                    return true;
                }
                catch
                {
                    _knownSize = -1;
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
