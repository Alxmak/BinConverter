using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class AboutViewModel : ObservableObject
    {
        [ObservableProperty] private string versionText;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCheck))]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        private bool isChecking;

        [ObservableProperty] private string updateStatusText = "";

        public bool CanCheck => !IsChecking;

        public AboutViewModel()
        {
            versionText = Strings.Format("About_VersionText", UpdateService.GetCurrentVersion());
        }

        // Пункт 5: та же логика, что и фоновая ежедневная проверка — если обновление
        // найдено, дальше им занимается общий баннер в ShellWindow (скачивание и тихая
        // установка), а не диалог с переходом в браузер, как было раньше.
        [RelayCommand(CanExecute = nameof(CanCheck))]
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            IsChecking = true;
            UpdateStatusText = Strings.Get("About_Checking");

            var result = await UpdateManager.Instance.CheckNowAsync();

            IsChecking = false;

            if (result == null) return;

            if (result.ErrorMessage != null)
            {
                UpdateStatusText = "";
                await DialogService.ShowWarningAsync(Strings.Get("About_CheckUpdatesTitle"), result.ErrorMessage);
                return;
            }

            UpdateStatusText = result.UpdateAvailable
                ? Strings.Format("About_UpdateAvailable", result.LatestVersion ?? "")
                : Strings.Get("About_UpToDate");
        }
    }
}
