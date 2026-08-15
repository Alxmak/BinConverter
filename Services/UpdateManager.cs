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
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Автообновление: раз в день тихо проверяет GitHub на новый релиз и показывает баннер
    /// в ShellWindow. Баннер проходит три состояния — «доступна версия» → «загрузка N%» →
    /// «загружено, установить». Он никогда не модальный и не мешает работать: установка
    /// запускается только по явному нажатию.
    ///
    /// Все решения («можно ли качать», «можно ли ставить») приняты в
    /// <see cref="UpdateFlow"/> из Core — он покрыт тестами. Здесь остались только эффекты:
    /// сеть, диск, запуск установщика и закрытие приложения. Главное правило —
    /// установщик не запускается, пока идёт конвертирование, сборка или проверка: иначе
    /// принудительное закрытие оборвало бы запись на середине и оставило битые файлы.
    /// </summary>
    public partial class UpdateManager : ObservableObject
    {
        public static UpdateManager Instance { get; } = new();

        private static readonly string StateDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware");

        private static readonly string LastCheckPath = Path.Combine(StateDir, "lastupdatecheck.txt");

        // Запоминаем скачанный установщик, чтобы после «Позже» и перезапуска предложить
        // установку сразу, не качая заново.
        private static readonly string DownloadedPath = Path.Combine(StateDir, "downloadedupdate.txt");

        private readonly UpdateFlow _flow = new();

        private UpdateCheckResult? _pending;
        private CancellationTokenSource? _downloadCts;

        [ObservableProperty] private string statusText = "";

        // ============ То, к чему привязан баннер ============

        public bool ShowUpdateBanner => _flow.IsBannerVisible;
        public bool IsUpdateAvailable => _flow.Stage == UpdateStage.Available;
        public bool IsDownloading => _flow.Stage == UpdateStage.Downloading;
        public bool IsReadyToInstall => _flow.Stage == UpdateStage.ReadyToInstall;

        /// <summary>Скачано, но идёт операция — показываем пояснение, почему кнопка неактивна.</summary>
        public bool IsWaitingForOperation => _flow.IsWaitingForOperation;

        public string BannerText => _flow.Stage switch
        {
            UpdateStage.Downloading => StatusText,
            UpdateStage.ReadyToInstall => Strings.Format("Shell_UpdateReadyBanner", _flow.LatestVersion),
            UpdateStage.Installing => Strings.Get("Shell_Installing"),
            _ => Strings.Format("Shell_UpdateBanner", _flow.LatestVersion)
        };

        private UpdateManager()
        {
            _flow.Changed += OnFlowChanged;

            // Пока идёт конвертирование/сборка/проверка, установка запрещена. Источник правды
            // об этом один на всё приложение — OperationLockService.
            OperationLockService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(OperationLockService.IsBusy))
                    _flow.SetOperationRunning(OperationLockService.Instance.IsBusy);
            };

            // Текст баннера собран кодом, а не привязкой {loc:Loc}, поэтому сам он при
            // смене языка не переведётся — надо попросить разметку перечитать его.
            // Случай не редкий: язык переключают в «Настройках», а баннер висит над
            // всеми разделами и виден как раз в этот момент. Отписка не нужна —
            // и служба локализации, и этот менеджер живут всё время работы программы.
            LocalizationService.Instance.LanguageChanged += () => OnPropertyChanged(nameof(BannerText));

            RestoreDownloadedInstaller();
        }

        private void OnFlowChanged()
        {
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(IsReadyToInstall));
            OnPropertyChanged(nameof(IsWaitingForOperation));
            OnPropertyChanged(nameof(BannerText));
            DownloadCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
            PostponeCommand.NotifyCanExecuteChanged();
            CancelDownloadCommand.NotifyCanExecuteChanged();
        }

        partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(BannerText));

        // ============ Проверка ============

        /// <summary>Вызывается при старте приложения — проверяет, только если сегодня ещё не проверяли.</summary>
        public Task<UpdateCheckResult?> CheckOnStartupAsync() =>
            ShouldCheckToday() ? CheckInternalAsync() : Task.FromResult<UpdateCheckResult?>(null);

        /// <summary>Вызывается по явному нажатию кнопки "Проверить обновления" — всегда проверяет.</summary>
        public Task<UpdateCheckResult?> CheckNowAsync() => CheckInternalAsync();

        private async Task<UpdateCheckResult?> CheckInternalAsync()
        {
            var result = await UpdateService.CheckForUpdateAsync();
            SaveLastCheckDate();

            if (result.ErrorMessage == null && result.UpdateAvailable && !string.IsNullOrEmpty(result.InstallerAssetUrl))
            {
                _pending = result;
                _flow.OnUpdateFound(result.LatestVersion ?? "");
                AppLogger.Log(Strings.Format("Shell_UpdateFoundLog", result.LatestVersion ?? "", result.CurrentVersion));
            }

            return result;
        }

        // ============ Команды баннера ============

        [RelayCommand(CanExecute = nameof(CanDownload))]
        private async Task DownloadAsync()
        {
            if (_pending?.InstallerAssetUrl == null) return;
            if (!_flow.TryBeginDownload()) return;

            _downloadCts = new CancellationTokenSource();
            try
            {
                AppLogger.Log(Strings.Format("Shell_UpdateDownloadStartLog", _flow.LatestVersion, _pending.InstallerAssetName ?? ""));

                StatusText = Strings.Format("Shell_DownloadingUpdate", 0.0);
                var progress = new Progress<double>(p => StatusText = Strings.Format("Shell_DownloadingUpdate", p));

                string installerPath = await UpdateService.DownloadInstallerAsync(
                    _pending.InstallerAssetUrl,
                    _pending.InstallerAssetName ?? "TweakFirmwareSetup.exe",
                    progress,
                    _downloadCts.Token);

                _flow.OnDownloadCompleted(installerPath);
                SaveDownloadedInstaller(_flow.LatestVersion, installerPath);
                AppLogger.Log(Strings.Get("Shell_UpdateDownloadedLog"));
            }
            catch (OperationCanceledException)
            {
                _flow.OnDownloadInterrupted();
                AppLogger.Log(Strings.Get("Shell_UpdateDownloadCancelledLog"));
            }
            catch (Exception ex)
            {
                _flow.OnDownloadInterrupted();
                AppLogger.Log(Strings.Format("Shell_UpdateErrorLog", ex.Message));
                await DialogService.ShowErrorAsync(Strings.Get("Shell_UpdateErrorTitle"), Strings.Format("Shell_UpdateErrorMessage", ex.Message));
            }
            finally
            {
                StatusText = "";
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        private bool CanDownload => _flow.CanDownload;

        [RelayCommand(CanExecute = nameof(CanCancelDownload))]
        private void CancelDownload() => _downloadCts?.Cancel();

        private bool CanCancelDownload => _flow.Stage == UpdateStage.Downloading;

        [RelayCommand(CanExecute = nameof(CanInstall))]
        private async Task InstallAsync()
        {
            // TryBeginInstall — единственное разрешение на запуск установщика. Оно вернёт
            // false, если в этот момент началась операция, даже если кнопка ещё не успела
            // стать неактивной.
            if (!_flow.TryBeginInstall()) return;

            string? installerPath = _flow.InstallerPath;
            if (installerPath == null || !File.Exists(installerPath))
            {
                _flow.OnInstallFailed();
                ForgetDownloadedInstaller();
                await DialogService.ShowErrorAsync(
                    Strings.Get("Shell_UpdateErrorTitle"),
                    Strings.Get("Shell_UpdateInstallerMissingMessage"));
                return;
            }

            try
            {
                StatusText = Strings.Get("Shell_Installing");

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

                ForgetDownloadedInstaller();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _flow.OnInstallFailed();
                StatusText = "";
                AppLogger.Log(Strings.Format("Shell_UpdateErrorLog", ex.Message));
                await DialogService.ShowErrorAsync(Strings.Get("Shell_UpdateErrorTitle"), Strings.Format("Shell_UpdateErrorMessage", ex.Message));
            }
        }

        private bool CanInstall => _flow.CanInstall;

        [RelayCommand(CanExecute = nameof(CanPostpone))]
        private void Postpone() => _flow.Postpone();

        private bool CanPostpone => _flow.Stage != UpdateStage.Downloading && _flow.Stage != UpdateStage.Installing;

        // ============ Память между запусками ============

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
                Directory.CreateDirectory(StateDir);
                File.WriteAllText(LastCheckPath, DateTime.Today.ToString("o", CultureInfo.InvariantCulture));
            }
            catch { /* не критично — просто проверим ещё раз при следующем запуске */ }
        }

        private static void SaveDownloadedInstaller(string version, string installerPath)
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                File.WriteAllText(DownloadedPath, version + "\n" + installerPath);
            }
            catch { /* не критично — в худшем случае скачаем заново */ }
        }

        private static void ForgetDownloadedInstaller()
        {
            try { if (File.Exists(DownloadedPath)) File.Delete(DownloadedPath); }
            catch { /* не критично */ }
        }

        private void RestoreDownloadedInstaller()
        {
            try
            {
                if (!File.Exists(DownloadedPath)) return;

                string[] lines = File.ReadAllLines(DownloadedPath);
                if (lines.Length < 2) { ForgetDownloadedInstaller(); return; }

                string version = lines[0].Trim();
                string installerPath = lines[1].Trim();

                // Файл мог быть вычищен из %TEMP%, а версия — уже установлена другим путём.
                // В обоих случаях запись бесполезна.
                if (!File.Exists(installerPath) || !VersionHelper.IsNewer(version, UpdateService.GetCurrentVersion()))
                {
                    ForgetDownloadedInstaller();
                    return;
                }

                _flow.RestoreDownloaded(version, installerPath);
            }
            catch { ForgetDownloadedInstaller(); }
        }

    }
}
