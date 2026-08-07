using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Operations;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class MergeViewModel : LogHostViewModel
    {
        [ObservableProperty] private string sourcePath = "";
        // Пусто до выбора файла: подсказку про перетаскивание убрали из карточки
        // "Общая информация" — там место для сведений о цепочке, а не для инструкции.
        [ObservableProperty] private string chainInfoText = "";

        // Карточка "Общая информация" из макета: размер файла, который получится после
        // склейки. Считается по уже найденной цепочке, отдельного прохода по диску не нужно.
        [ObservableProperty] private string resultSizeText = Strings.Format("Merge_ResultSizeLine", NoValuePlaceholder);
        [ObservableProperty] private string outputPath = "";
        [ObservableProperty] private string expectedHashText = "";

        [ObservableProperty] private bool openFolderAfter = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private bool isBusy;

        [ObservableProperty] private bool isPaused;
        [ObservableProperty] private string pauseButtonText = Strings.Get("Common_PauseButton");

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;

        public bool CanStart => !IsBusy;

        /// <summary>Сборку можно ставить на паузу всё время, пока она идёт: отдельной
        /// фазы проверки хэша, как в Конвертировании, здесь нет.</summary>
        public bool CanPause => IsBusy;

        /// <summary>Пока идёт сборка, поля и параметры вкладки недоступны.</summary>
        public bool IsNotBusy => !IsBusy;

        private CancellationTokenSource? _cts;
        private PauseController? _pauseController;

        // Пункт: "Папка назначения" должна показывать путь по умолчанию сразу при запуске
        // (как в Конвертировании), а не только после выбора файла цепочки. Пока путь не
        // менялся пользователем явно (набор текста/вставка/"Обзор..."), автоматически
        // подставляем актуальный путь по умолчанию — сначала общий, потом с именем исходника.
        private bool _outputPathIsAuto = true;
        private bool _settingOutputPathInternally;

        public MergeViewModel()
        {
            SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), "emmc_merged.bin"));
        }

        public void Detach() { }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(IsNotBusy));
        }

        // Пункт: поле пути теперь редактируется свободно (можно вставлять и печатать) —
        // цепочка частей должна пересчитываться и при прямом вводе, не только через SetSource.
        partial void OnSourcePathChanged(string value) => TryResolveChain();

        partial void OnOutputPathChanged(string value)
        {
            if (!_settingOutputPathInternally) _outputPathIsAuto = false;
        }

        private void SetOutputPathAuto(string path)
        {
            _settingOutputPathInternally = true;
            OutputPath = path;
            _settingOutputPathInternally = false;
        }

        [RelayCommand]
        private void BrowseSource()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Merge_SourceFileFilter") };
            if (dlg.ShowDialog() == true) SetSource(dlg.FileName);
        }

        public void SetSource(string path)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(Strings.Get("Common_FileNotFoundMessage"), Strings.Get("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SourcePath = path;
        }

        private void TryResolveChain()
        {
            try
            {
                // Тот же расчёт, что делает сама сборка перед проверкой места, —
                // считаем его один раз и в одном месте.
                long total = MergeOperation.GetChainSize(SourcePath, out var chain);

                ChainInfoText = Strings.Format("Merge_ChainFoundInfo", chain.Count, SizeFormatHelper.Format(total), Path.GetFileName(chain[0]));
                ResultSizeText = Strings.Format("Merge_ResultSizeLine", SizeFormatHelper.Format(total));

                if (_outputPathIsAuto)
                {
                    string name = MergeOutputNaming.SuggestFileName(HashHelper.ResolveBasePath(SourcePath));
                    SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), name));
                }
            }
            catch (Exception ex)
            {
                ChainInfoText = Strings.Format("Merge_ChainResolveError", ex.Message);
                ResultSizeText = Strings.Format("Merge_ResultSizeLine", NoValuePlaceholder);
            }
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new SaveFileDialog { Filter = Strings.Get("Common_BinFileFilter"), FileName = "emmc_merged.bin" };
            if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            var request = new MergeRequest
            {
                AnyChainFilePath = SourcePath,
                OutputPath = OutputPath,
                ExpectedHash = ExpectedHashText
            };

            _cts = new CancellationTokenSource();
            _pauseController = new PauseController();

            var progress = new Progress<MergeProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesRead / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double totalPct = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = totalPct;
                CurrentFileLabel = Strings.Format("Common_FileProgressLabel", p.CurrentFileName, p.CurrentFileIndex, p.TotalFiles);
            });

            try
            {
                var outcome = await MergeOperation.RunAsync(
                    request, new DialogConflictResolver(), progress,
                    AppLogger.Log, _pauseController, _cts.Token, MarkOperationStarted);

                // При конфликте операция могла уйти в соседнее имя — показываем, куда именно.
                if (outcome.OutputPathChanged) OutputPath = outcome.OutputPath;

                await ShowOutcomeAsync(outcome);
            }
            finally
            {
                OverallProgress = 0; CurrentFileProgress = 0;
                IsBusy = false;
                IsPaused = false;
                OperationLockService.Instance.IsBusy = false;
                _cts = null;
                _pauseController = null;
            }
        }

        /// <summary>Вызывается операцией, когда все проверки прошли и работа началась.</summary>
        private void MarkOperationStarted()
        {
            IsBusy = true;
            IsPaused = false;
            OperationLockService.Instance.IsBusy = true;
            PauseButtonText = Strings.Get("Common_PauseButton");
            OverallProgress = 0; CurrentFileProgress = 0;
        }

        private async Task ShowOutcomeAsync(MergeOutcome outcome)
        {
            switch (outcome.Status)
            {
                case MergeStatus.SourceNotFound:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"), Strings.Get("Merge_SelectSourceFirst"));
                    break;

                case MergeStatus.OutputPathNotSpecified:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"), Strings.Get("Merge_SpecifyOutputFile"));
                    break;

                case MergeStatus.CancelledBeforeStart:
                    // Пользователь сам отказался перезаписывать файл — сообщать ему об этом нечего.
                    break;

                case MergeStatus.ChainResolveFailed:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"),
                        Strings.Format("Merge_ChainCheckErrorMessage", outcome.ErrorMessage));
                    break;

                case MergeStatus.NotEnoughSpace:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_LowSpaceTitle"),
                        Strings.Format("Merge_LowSpaceMessage",
                            SizeFormatHelper.Format(outcome.ChainSizeBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.RequiredBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.AvailableBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.MissingBytes)));
                    break;

                case MergeStatus.HashMatch:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Format("Merge_ResultMatchMessage", HashDisplay.Wrap(outcome.MergedHash)));
                    OpenResultFolder(outcome.OutputPath);
                    break;

                case MergeStatus.HashMismatch:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_VerifyErrorTitle"),
                        Strings.Format("Merge_ResultMismatchMessage", HashDisplay.Wrap(ExpectedHashText.Trim()), HashDisplay.Wrap(outcome.MergedHash)));
                    OpenResultFolder(outcome.OutputPath);
                    break;

                case MergeStatus.Completed:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Format("Merge_DoneMessage", outcome.PartsUsed, outcome.TotalBytes, HashDisplay.Wrap(outcome.MergedHash)));
                    OpenResultFolder(outcome.OutputPath);
                    break;

                case MergeStatus.Cancelled:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_CancelledTitle"), Strings.Get("Merge_CancelledMessage"));
                    break;

                case MergeStatus.Failed:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), outcome.DiskFull
                        ? Strings.Get("Merge_DiskFullMessage")
                        : Strings.Format("Merge_ErrorMessage", outcome.ErrorMessage));
                    break;
            }
        }

        private void OpenResultFolder(string outputPath)
        {
            if (!OpenFolderAfter) return;

            string? folder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(folder)) return;

            try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Log(Strings.Format("Convert_OpenFolderFailedLog", ex.Message)); }
        }
        [RelayCommand]
        private void Cancel()
        {
            _pauseController?.Resume();
            _cts?.Cancel();
        }

        [RelayCommand]
        private void TogglePause()
        {
            if (_pauseController == null) return;

            if (IsPaused)
            {
                _pauseController.Resume();
                IsPaused = false;
                PauseButtonText = Strings.Get("Common_PauseButton");
                AppLogger.Log(Strings.Get("Merge_ResumedLog"));
            }
            else
            {
                _pauseController.Pause();
                IsPaused = true;
                PauseButtonText = Strings.Get("Common_ResumeButton");
                AppLogger.Log(Strings.Get("Merge_PausedLog"));
            }
        }

    }
}
