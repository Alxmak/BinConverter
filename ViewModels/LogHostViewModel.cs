using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// Общая часть трёх рабочих вкладок: журнал и прочерк в незаполненных строках.
    ///
    /// Журнал один на всё приложение (<see cref="LogService"/>), поэтому и команды к нему
    /// у всех вкладок были одинаковые — три копии одного кода, которые разъезжались:
    /// в «Конвертировании» и «Сборке» ошибка сохранения показывалась системным
    /// MessageBox, в «Проверке» — диалогом в стиле программы. Оставлен второй вариант:
    /// <see cref="DialogService"/> и есть то единое место, через которое по договорённости
    /// идут все всплывающие сообщения.
    /// </summary>
    public abstract partial class LogHostViewModel : LocalizedViewModel
    {
        /// <summary>Проброс общей коллекции, а не своя копия на каждую вкладку.</summary>
        public ObservableCollection<string> LogLines => LogService.Lines;

        [RelayCommand]
        private void OpenLog() => AppLogger.OpenLogFile();

        [RelayCommand]
        private void SaveLog()
        {
            var dlg = new SaveFileDialog { Filter = Strings.Get("Common_TextFileFilter"), FileName = "TweakFirmware.log.txt" };
            if (dlg.ShowDialog() != true) return;

            try
            {
                LogService.SaveAs(dlg.FileName);
            }
            catch (Exception ex)
            {
                _ = DialogService.ShowErrorAsync(Strings.Get("Common_Error"), Strings.Format("Common_SaveLogFailed", ex.Message));
            }
        }

        [RelayCommand]
        private void ClearLog() => LogService.Clear();
    }
}
