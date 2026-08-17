using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// Общая часть трёх рабочих вкладок: журнал и прочерк в незаполненных строках.
    ///
    /// Журнал один на всё приложение (<see cref="LogService"/>), поэтому и команды к нему
    /// у всех вкладок были одинаковые — три копии одного кода, которые разъезжались:
    /// в «Конвертировании» и «Сборке» ошибка сохранения показывалась системным
    /// MessageBox, в «Проверке» — диалогом в стиле программы. Сами действия теперь живут
    /// в <see cref="LogActions"/>: те же три кнопки есть и в «Настройках», и разъехаться
    /// они больше не могут.
    /// </summary>
    public abstract partial class LogHostViewModel : LocalizedViewModel
    {
        /// <summary>Проброс общей коллекции, а не своя копия на каждую вкладку.</summary>
        public ObservableCollection<string> LogLines => LogService.Lines;

        [RelayCommand]
        private void OpenLog() => LogActions.Open();

        [RelayCommand]
        private void SaveLog() => LogActions.SaveAs();

        [RelayCommand]
        private void ClearLog() => LogActions.Clear();
    }
}
