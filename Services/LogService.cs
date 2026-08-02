using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BinConverter.Core;

namespace BinConverter.Services
{
    /// <summary>
    /// Единственный на всё приложение источник строк журнала. Подписка на AppLogger
    /// происходит один раз (статический конструктор) — поэтому история не пропадает
    /// при переключении между разделами и хранит всё с момента запуска программы.
    /// </summary>
    public static class LogService
    {
        public static ObservableCollection<string> Lines { get; } = new();

        static LogService()
        {
            AppLogger.LogWritten += line => Application.Current?.Dispatcher.BeginInvoke(() => Lines.Add(line));
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
            File.Copy(AppLogger.LogFilePath, destinationPath, overwrite: true);
        }
    }
}
