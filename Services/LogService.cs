using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using TweakFirmware.Core;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Одна запись журнала.
    ///
    /// Отдельный тип, а не просто строка, и это не украшательство. Список на экране
    /// сравнивает элементы, когда его просят найти выделенное или показать нужный
    /// элемент, — а строки сравниваются по содержимому. Записи журнала повторяются
    /// дословно (отметка времени идёт с точностью до секунды, а сообщения бывают
    /// одинаковыми), и выделив одну строку, человек получал в буфере обмена и её
    /// близнеца. У объекта же каждая запись отличается от любой другой сама по себе.
    /// </summary>
    public sealed class LogLine
    {
        public LogLine(string text) => Text = text;

        public string Text { get; }

        /// <summary>На случай, если запись где-то попадёт в строку помимо привязки.</summary>
        public override string ToString() => Text;
    }

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

        public static ObservableCollection<LogLine> Lines { get; } = new();

        static LogService()
        {
            AppLogger.LogWritten += line => Application.Current?.Dispatcher.BeginInvoke(() => Append(line));
        }

        /// <summary>
        /// Сколько строк снимается за один раз, когда предел превышен. Пачкой, а не
        /// по одной на каждую добавленную: каждое удаление — отдельное уведомление
        /// подписчикам, то есть на список на экране приходилось бы по два изменения
        /// на строку вместо одного, причём подряд. Именно такие сдвоенные изменения
        /// и валили список с «ItemsControl не соответствует своему источнику элементов».
        /// </summary>
        private const int TrimBatchSize = 500;

        private static void Append(string line)
        {
            Lines.Add(new LogLine(line));

            if (Lines.Count <= MaxVisibleLines + TrimBatchSize) return;

            for (int i = 0; i < TrimBatchSize; i++) Lines.RemoveAt(0);
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
