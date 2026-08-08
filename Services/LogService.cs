using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using TweakFirmware.Core;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Единственный на всё приложение источник строк журнала. Подписка на AppLogger
    /// происходит один раз (статический конструктор) — поэтому история не пропадает
    /// при переключении между разделами и хранит всё с момента запуска программы.
    /// </summary>
    public static class LogService
    {
        /// <summary>
        /// Предел числа строк на экране. Коллекция общая на всё приложение и живёт до
        /// закрытия программы, то есть росла без границы ровно как и файл журнала:
        /// большая операция добавляет тысячи строк. Полная история остаётся в файле —
        /// его открывает кнопка «Открыть».
        /// </summary>
        public const int MaxVisibleLines = 5000;

        public static ObservableCollection<string> Lines { get; } = new();

        static LogService()
        {
            AppLogger.LogWritten += line => Application.Current?.Dispatcher.BeginInvoke(() => Append(line));
        }

        private static void Append(string line)
        {
            Lines.Add(line);

            // По одной за раз: строки добавляются по одной, поэтому и лишняя всегда одна.
            if (Lines.Count > MaxVisibleLines) Lines.RemoveAt(0);
        }

        /// <summary>Нужно дёрнуть один раз при старте приложения, чтобы статический конструктор
        /// точно отработал до первой записи в лог (иначе первая строка может быть потеряна).</summary>
        public static void EnsureInitialized() { _ = Lines; }

        public static void Clear()
        {
            Application.Current?.Dispatcher.Invoke(() => Lines.Clear());
            AppLogger.ClearLog();
        }

        public static void SaveAs(string destinationPath)
        {
            // Файла может не быть: записи в него могли не удаваться (папка только для
            // чтения, занят антивирусом) — журнал при этом всё равно виден на экране.
            AppLogger.EnsureFileExists();
            File.Copy(AppLogger.LogFilePath, destinationPath, overwrite: true);
        }
    }
}
