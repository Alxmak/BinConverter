using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            versionText = $"Версия {UpdateService.GetCurrentVersion()}";
        }

        [RelayCommand(CanExecute = nameof(CanCheck))]
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            IsChecking = true;
            UpdateStatusText = "Проверка...";

            var result = await UpdateService.CheckForUpdateAsync();

            IsChecking = false;

            if (result.ErrorMessage != null)
            {
                UpdateStatusText = "";
                await DialogService.ShowWarningAsync("Проверка обновлений", result.ErrorMessage);
                return;
            }

            if (result.UpdateAvailable)
            {
                UpdateStatusText = $"Доступна новая версия: {result.LatestVersion}";
                var choice = await DialogService.ShowConfirmAsync(
                    "Доступно обновление",
                    $"Текущая версия: {result.CurrentVersion}\nНовая версия: {result.LatestVersion}\n\nОткрыть страницу загрузки?",
                    "Открыть", null, "Позже");

                if (choice == DialogChoice.Primary && !string.IsNullOrEmpty(result.ReleaseUrl))
                    Process.Start(new ProcessStartInfo { FileName = result.ReleaseUrl, UseShellExecute = true });
            }
            else
            {
                UpdateStatusText = "У вас последняя версия.";
                await DialogService.ShowInfoAsync("Проверка обновлений", $"У вас установлена последняя версия ({result.CurrentVersion}).");
            }
        }
    }
}
