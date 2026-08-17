using System;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Три действия над журналом: открыть файл, сохранить копию, очистить.
    ///
    /// Вынесены сюда, потому что делают их из двух разных мест: карточка «Журнал»
    /// на рабочих вкладках (LogHostViewModel) и строка «Журнал приложения»
    /// в «Настройках». Журнал в программе один
    /// (<see cref="LogService"/>), и действия над ним должны быть одни — вторая копия
    /// того же кода уже однажды разъехалась: в двух вкладках ошибка сохранения
    /// показывалась системным MessageBox, а в третьей диалогом в стиле программы.
    /// </summary>
    public static class LogActions
    {
        /// <summary>Открывает файл журнала тем, чем система открывает текстовые файлы.</summary>
        public static void Open() => AppLogger.OpenLogFile();

        /// <summary>
        /// Спрашивает, куда положить копию журнала, и пишет её. Отказ от диалога —
        /// это не ошибка, а передумали: молча ничего не делаем.
        /// </summary>
        public static void SaveAs()
        {
            var dlg = new SaveFileDialog
            {
                Filter = Strings.Get("Common_TextFileFilter"),
                FileName = "TweakFirmware.log.txt"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                LogService.SaveAs(dlg.FileName);
            }
            catch (Exception ex)
            {
                _ = DialogService.ShowErrorAsync(Strings.Get("Common_Error"),
                    Strings.Format("Common_SaveLogFailed", ex.Message));
            }
        }

        /// <summary>
        /// Чистит и файл на диске, и то, что показано в карточке «Журнал». Раньше
        /// в «Настройках» вызывался AppLogger.ClearLog напрямую: файл становился пустым,
        /// а на экране оставались все прежние строки, и «Сохранить» отдавал файл,
        /// не совпадающий с тем, что человек видит.
        /// </summary>
        public static void Clear() => LogService.Clear();
    }
}
