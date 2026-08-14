using System;
using System.Diagnostics;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Открывает папку с результатом после операции. Одинаковый код с одинаковым разбором
    /// неудачи лежал в двух вкладках, а с «Извлечением разделов» стал бы третьим.
    /// </summary>
    public static class ResultFolder
    {
        /// <summary>
        /// Неудача сюда не выносится: папку открывают в конце удачной работы, и падать
        /// или мешать окном из-за того, что проводник не запустился, незачем — запись
        /// в журнале говорит достаточно.
        /// </summary>
        public static void Open(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_OpenFolderFailedLog", ex.Message));
            }
        }
    }
}
