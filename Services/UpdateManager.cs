using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Core;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Единая на всё приложение точка автообновления: раз в день (не при каждом запуске)
    /// тихо проверяет GitHub на новый релиз и, если он есть, показывает баннер в ShellWindow
    /// с кнопками "Обновить сейчас"/"Позже". Обновление скачивается и ставится тихо (silent
    /// install), без перехода в браузер — после запуска установщика само приложение
    /// закрывается, чтобы освободить свой exe-файл для перезаписи.
    /// </summary>
    public partial class UpdateManager : ObservableObject
    {
        public static UpdateManager Instance { get; } = new();

        private static readonly string LastCheckPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "lastupdatecheck.txt");

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        private string latestVersion = "";

        [ObservableProperty] private bool showUpdateBanner;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanInteract))]
        [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
        [NotifyCanExecuteChangedFor(nameof(DismissCommand))]
        private bool isBusy;

        [ObservableProperty] private string statusText = "";

        public bool CanInteract => !IsBusy;
        public string BannerText => $"Доступна новая версия {LatestVersion}";

        private UpdateCheckResult? _pending;

        private UpdateManager() { }

        /// <summary>Вызывается при старте приложения — проверяет, только если сегодня ещё не проверяли.</summary>
        public Task<UpdateCheckResult?> CheckOnStartupAsync() =>
            ShouldCheckToday() ? CheckInternalAsync() : Task.FromResult<UpdateCheckResult?>(null);

        /// <summary>Вызывается по явному нажатию кнопки "Проверить обновления" — всегда проверяет.</summary>
        public Task<UpdateCheckResult?> CheckNowAsync() => CheckInternalAsync();

        private bool ShouldCheckToday()
        {
            try
            {
                if (!File.Exists(LastCheckPath)) return true;
                string text = File.ReadAllText(LastCheckPath).Trim();
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
                    return last.Date < DateTime.Today;
            }
            catch { /* при сомнении — лучше проверить лишний раз, чем не проверить вовсе */ }
            return true;
        }

        private static void SaveLastCheckDate()
        {
            try
            {
                string? dir = Path.GetDirectoryName(LastCheckPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(LastCheckPath, DateTime.Today.ToString("o", CultureInfo.InvariantCulture));
            }
            catch { /* не критично — просто проверим ещё раз при следующем запуске */ }
        }

        private async Task<UpdateCheckResult?> CheckInternalAsync()
        {
            var result = await UpdateService.CheckForUpdateAsync();
            SaveLastCheckDate();

            if (result.ErrorMessage == null && result.UpdateAvailable && !string.IsNullOrEmpty(result.InstallerAssetUrl))
            {
                _pending = result;
                LatestVersion = result.LatestVersion ?? "";
                ShowUpdateBanner = true;
                AppLogger.Log($"Найдено обновление: версия {result.LatestVersion} (текущая {result.CurrentVersion})");
            }

            return result;
        }

        [RelayCommand(CanExecute = nameof(CanInteract))]
        private void Dismiss() => ShowUpdateBanner = false;

        [RelayCommand(CanExecute = nameof(CanInteract))]
        private async Task UpdateNowAsync()
        {
            if (_pending?.InstallerAssetUrl == null) return;

            IsBusy = true;
            try
            {
                AppLogger.Log($"Начата загрузка обновления {LatestVersion}: {_pending.InstallerAssetName}");

                var progress = new Progress<double>(p => StatusText = $"Загрузка обновления... {p:F0}%");
                StatusText = "Загрузка обновления...";

                string installerPath = await UpdateService.DownloadInstallerAsync(
                    _pending.InstallerAssetUrl,
                    _pending.InstallerAssetName ?? "TweakFirmwareSetup.exe",
                    progress,
                    CancellationToken.None);

                StatusText = "Установка...";
                AppLogger.Log("Обновление скачано, запуск тихой установки — приложение перезапустится автоматически");

                // /VERYSILENT + /SUPPRESSMSGBOXES — без единого окна установщика.
                // /CLOSEAPPLICATIONS — подстраховка от Inno Setup, если наше приложение
                // всё-таки не успеет закрыться раньше, чем начнётся копирование файлов
                // (основной механизм — Application.Current.Shutdown() чуть ниже).
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS",
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                IsBusy = false;
                StatusText = "";
                AppLogger.Log($"Ошибка автообновления: {ex.Message}");
                await DialogService.ShowErrorAsync("Ошибка обновления", $"Не удалось скачать или установить обновление:\n{ex.Message}");
            }
        }
    }
}
