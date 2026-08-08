using System;
using System.Windows;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Запись в буфер обмена, которая не может уронить программу.
    ///
    /// Буфер в Windows — общий на всю систему ресурс: пока им владеет другое
    /// приложение, запись падает с COM-ошибкой. Причина не в нас и делать из неё
    /// событие незачем, поэтому неудача только пишется в журнал, а вызывающий код
    /// получает false и решает сам, сообщать ли о ней.
    ///
    /// Собрано в одно место потому, что копирование понадобилось уже трижды:
    /// строка журнала, письмо в «Обратной связи» и хэш в окне с итогом.
    /// </summary>
    public static class ClipboardHelper
    {
        public static bool TryCopy(string text)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_ClipboardFailedLog", ex.Message));
                return false;
            }
        }
    }
}
