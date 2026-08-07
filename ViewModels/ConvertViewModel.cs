using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Operations;
using TweakFirmware.Models;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class ConvertViewModel : LogHostViewModel
    {
        private const int CollapsedFileListCount = 4;
        private const int MaxFilesToEnumerate = 2000;

        public ObservableCollection<ProgrammerPreset> Presets { get; } = new()
        {
            new ProgrammerPreset("TNM5000", FileSplitter.DefaultMaxPartSizeBytes),
            new ProgrammerPreset("RT809H", FileSplitter.DefaultMaxPartSizeBytes),
            new ProgrammerPreset(Strings.Get("Convert_CustomPresetName"), null)
        };

        [ObservableProperty] private ProgrammerPreset selectedPreset = null!;

        [ObservableProperty] private string sourcePath = "";
        [ObservableProperty] private string outputFolder = "";
        [ObservableProperty] private string baseFileName = "emmc.bin";

        [ObservableProperty] private string customLimitBytesText = FileSplitter.DefaultMaxPartSizeBytes.ToString();

        [ObservableProperty] private string generalInfoText = "";
        [ObservableProperty] private string displayedFilesText = "";
        [ObservableProperty] private bool showExpandButton;
        [ObservableProperty] private bool showAllFiles;
        [ObservableProperty] private string expandButtonText = Strings.Get("Convert_ShowAllFilesButton");

        [ObservableProperty] private bool verifyHashAfter = true;
        [ObservableProperty] private bool openFolderAfter = true;
        [ObservableProperty] private bool checkDiskSpace = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private bool isBusy;

        [ObservableProperty] private bool isPaused;
        [ObservableProperty] private string pauseButtonText = Strings.Get("Common_PauseButton");
        [ObservableProperty] private bool isVerifying;

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;
        [ObservableProperty] private double shaProgress;
        [ObservableProperty] private string statusText = Strings.Get("Common_Ready");

        public bool CanStart => !IsBusy;
        public bool CanPause => IsBusy && !IsVerifying;

        /// <summary>Пока идёт операция, поля и параметры вкладки недоступны — менять их
        /// на ходу нельзя, иначе настройки разойдутся с уже запущенным процессом.</summary>
        public bool IsNotBusy => !IsBusy;
        public bool IsCustomPreset => SelectedPreset?.IsCustom == true;

        private CancellationTokenSource? _cts;
        private PauseController? _pauseController;

        private int _expectedFileCount;
        private long _expectedTotalSize;
        private long _expectedLimitBytes;

        public ConvertViewModel()
        {
            SelectedPreset = Presets[0];
            OutputFolder = GetDefaultOutputFolder();
        }

        /// <summary>Больше не требуется — журнал общий и не привязан к жизни этой ViewModel,
        /// но метод оставлен для совместимости точки вызова из code-behind.</summary>
        public void Detach() { }

        private static string GetDefaultOutputFolder() => OutputPathSettingsService.GetConvertFolder();

        // Разбор лимита — в PartSizeLimit из Core: от этого числа зависит, на сколько частей
        // разрежется прошивка, поэтому граничные случаи проверяются тестами.
        private long CurrentLimitBytes =>
            PartSizeLimit.Resolve(SelectedPreset?.MaxPartSizeBytes, CustomLimitBytesText);

        partial void OnSelectedPresetChanged(ProgrammerPreset value)
        {
            OnPropertyChanged(nameof(IsCustomPreset));
            if (value?.MaxPartSizeBytes is long fixedBytes)
                CustomLimitBytesText = fixedBytes.ToString();
            UpdatePreview();
        }

        partial void OnCustomLimitBytesTextChanged(string value) => UpdatePreview();
        // Пункт: поле пути теперь редактируется свободно (можно вставлять и печатать) —
        // предпросмотр должен обновляться и при прямом вводе, не только через SetSource.
        partial void OnSourcePathChanged(string value) => UpdatePreview();
        partial void OnShowAllFilesChanged(bool value) => RebuildFilesText();
        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(IsNotBusy));
        }
        partial void OnIsVerifyingChanged(bool value) => OnPropertyChanged(nameof(CanPause));

        [RelayCommand]
        private void BrowseSource()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Common_BinFileFilter") };
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

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new OpenFolderDialog { Title = Strings.Get("Convert_ChooseOutputFolderTitle") };
            if (dlg.ShowDialog() == true) SetOutputFolder(dlg.FolderName);
        }

        /// <summary>Используется и кнопкой "Обзор...", и вставкой пути из буфера обмена.</summary>
        public void SetOutputFolder(string path)
        {
            OutputFolder = path;
            UpdatePreview();
        }

        [RelayCommand]
        private void ToggleExpand() => ShowAllFiles = !ShowAllFiles;

        // ============================= Предпросмотр =============================

        // Пункт 5: строки всегда на экране — до выбора файла напротив них прочерк
        // (NoValuePlaceholder — из LogHostViewModel, он общий для всех вкладок).

        private void UpdatePreview()
        {
            long limit = CurrentLimitBytes;

            if (!File.Exists(SourcePath))
            {
                GeneralInfoText =
                    Strings.Format("Convert_SourceSizeLine", NoValuePlaceholder) + "\n" +
                    Strings.Format("Convert_ExpectedCountLine", NoValuePlaceholder);
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = Strings.Get("Convert_EachFileSizeHeader") + " " + NoValuePlaceholder;
                return;
            }

            long size = new FileInfo(SourcePath).Length;

            if (limit < 1024)
            {
                GeneralInfoText =
                    Strings.Format("Convert_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                    Strings.Format("Convert_ExpectedCountLine", NoValuePlaceholder) + Strings.Get("Convert_InvalidLimitSuffix");
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = Strings.Get("Convert_EachFileSizeHeader") + " " + NoValuePlaceholder;
                return;
            }

            int count = FileSplitter.CalculateExpectedPartCount(size, limit);

            if (count > MaxFilesToEnumerate)
            {
                GeneralInfoText =
                    Strings.Format("Convert_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                    Strings.Format("Convert_ExpectedCountLine", count.ToString("N0")) + Strings.Get("Convert_TooManyFilesSuffix");
                _expectedFileCount = count;
                ShowExpandButton = false;
                DisplayedFilesText = Strings.Get("Convert_EachFileSizeHeader") + " " + NoValuePlaceholder;
                return;
            }

            _expectedFileCount = count;
            _expectedTotalSize = size;
            _expectedLimitBytes = limit;

            GeneralInfoText =
                Strings.Format("Convert_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                Strings.Format("Convert_ExpectedCountLine", count);

            ShowExpandButton = count > CollapsedFileListCount;
            ShowAllFiles = false;
            RebuildFilesText();
        }

        private void RebuildFilesText()
        {
            if (_expectedFileCount == 0) { DisplayedFilesText = Strings.Get("Convert_EachFileSizeHeader") + " " + NoValuePlaceholder; return; }

            int toShow = ShowAllFiles ? _expectedFileCount : Math.Min(CollapsedFileListCount, _expectedFileCount);

            var sb = new StringBuilder();
            sb.AppendLine(Strings.Get("Convert_EachFileSizeHeader"));
            long remaining = _expectedTotalSize;
            for (int i = 1; i <= _expectedFileCount; i++)
            {
                long partSize = Math.Min(_expectedLimitBytes, remaining);
                remaining -= partSize;
                if (i > toShow) continue;
                sb.AppendLine(Strings.Format("Convert_FileSizeLine", i, SizeFormatHelper.Format(partSize)));
            }

            if (!ShowAllFiles && _expectedFileCount > CollapsedFileListCount)
                sb.AppendLine(Strings.Format("Convert_MoreFilesLine", _expectedFileCount - CollapsedFileListCount));

            ExpandButtonText = ShowAllFiles ? Strings.Get("Convert_CollapseList") : Strings.Format("Convert_ShowAllFilesCount", _expectedFileCount);
            DisplayedFilesText = sb.ToString().TrimEnd();
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            var request = new ConvertRequest
            {
                SourcePath = SourcePath,
                OutputFolder = OutputFolder,
                BaseFileName = BaseFileName,
                MaxPartSizeBytes = CurrentLimitBytes,
                VerifyHash = VerifyHashAfter,
                CheckDiskSpace = CheckDiskSpace
            };

            _cts = new CancellationTokenSource();
            _pauseController = new PauseController();

            // Общая работа: при проверке хэша файл читается ещё раз, поэтому байт вдвое больше.
            long sourceSize = File.Exists(SourcePath) ? new FileInfo(SourcePath).Length : 0;
            long totalWorkBytes = sourceSize * (VerifyHashAfter ? 2 : 1);

            var splitProgress = new Progress<SplitProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesWritten / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)p.TotalBytesWritten / totalWorkBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = overallPct;
                CurrentFileLabel = Strings.Format("Common_FileProgressLabel", p.CurrentFileName, p.CurrentFileIndex, p.TotalFiles);
            });

            var hashProgress = new Progress<(long done, long total)>(p =>
            {
                if (!IsVerifying)
                {
                    IsVerifying = true;
                    // Фаза проверки хэша паузу не поддерживает, и оставленная пауза выглядела бы
                    // как зависший бар — снимаем её сами.
                    if (IsPaused) { _pauseController?.Resume(); IsPaused = false; PauseButtonText = Strings.Get("Common_PauseButton"); }
                    StatusText = Strings.Get("Common_VerifyingHash");
                }
                ShaProgress = p.total > 0 ? (double)p.done / p.total * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)(sourceSize + p.done) / totalWorkBytes * 100.0 : 100.0;
                OverallProgress = Math.Min(100.0, overallPct);
            });

            try
            {
                var outcome = await ConvertOperation.RunAsync(
                    request, new DialogConflictResolver(), splitProgress, hashProgress,
                    AppLogger.Log, _pauseController, _cts.Token, MarkOperationStarted);

                // При конфликте операция могла уйти в соседнюю папку — показываем, куда именно.
                if (outcome.OutputFolderChanged) OutputFolder = outcome.OutputFolder;

                await ShowOutcomeAsync(outcome);
            }
            finally
            {
                OverallProgress = 0; CurrentFileProgress = 0; ShaProgress = 0;
                IsBusy = false;
                IsPaused = false;
                IsVerifying = false;
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
            IsVerifying = false;
            OperationLockService.Instance.IsBusy = true;
            PauseButtonText = Strings.Get("Common_PauseButton");
            OverallProgress = 0; CurrentFileProgress = 0; ShaProgress = 0;
            StatusText = Strings.Get("Convert_Started");
        }

        /// <summary>
        /// Единственное место, где итог операции превращается в текст и диалоги.
        /// Сама операция про интерфейс ничего не знает.
        /// </summary>
        private async Task ShowOutcomeAsync(ConvertOutcome outcome)
        {
            switch (outcome.Status)
            {
                case ConvertStatus.SourceNotFound:
                    StatusText = Strings.Get("Convert_SelectSourceFirst");
                    break;

                case ConvertStatus.OutputFolderNotSpecified:
                    StatusText = Strings.Get("Convert_SpecifyOutputFolder");
                    break;

                case ConvertStatus.InvalidPartSize:
                    StatusText = Strings.Get("Convert_InvalidPartSize");
                    break;

                case ConvertStatus.CancelledBeforeStart:
                    // Пользователь сам отказался в диалоге конфликта — статус не трогаем.
                    break;

                case ConvertStatus.NotEnoughSpace:
                    await DialogService.ShowWarningAsync(Strings.Get("Convert_LowSpaceTitle"),
                        Strings.Format("Convert_LowSpaceMessage",
                            SizeFormatHelper.Format(outcome.SourceSizeBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.RequiredBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.AvailableBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.MissingBytes)));
                    break;

                case ConvertStatus.CompletedVerified:
                    // Пункт 1: статус-строку не дублируем текстом успеха — итог виден в диалоге.
                    StatusText = Strings.Get("Common_Done");
                    await DialogService.ShowInfoAsync(Strings.Get("Convert_DoneTitle"),
                        Strings.Format("Convert_DoneVerifiedMessage", outcome.PartsCreated, HashDisplay.Wrap(outcome.SourceHash)));
                    OpenResultFolder(outcome.OutputFolder);
                    break;

                case ConvertStatus.HashMismatch:
                    StatusText = Strings.Get("Convert_HashCheckFailed");
                    await DialogService.ShowErrorAsync(Strings.Get("Convert_VerifyErrorTitle"),
                        Strings.Format("Convert_VerifyErrorMessage", HashDisplay.Wrap(outcome.SourceHash), HashDisplay.Wrap(outcome.RecombinedHash)));
                    OpenResultFolder(outcome.OutputFolder);
                    break;

                case ConvertStatus.Completed:
                    StatusText = Strings.Get("Common_Done");
                    await DialogService.ShowInfoAsync(Strings.Get("Convert_DoneTitle"),
                        Strings.Format("Convert_DoneMessage", outcome.PartsCreated));
                    OpenResultFolder(outcome.OutputFolder);
                    break;

                case ConvertStatus.Cancelled:
                    StatusText = Strings.Get("Convert_CancelledStatus");
                    await DialogService.ShowInfoAsync(Strings.Get("Convert_CancelledTitle"),
                        Strings.Format("Convert_CancelledMessage", outcome.CreatedFileCount));
                    break;

                case ConvertStatus.Failed:
                    StatusText = Strings.Get("Convert_ErrorStatus");
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), outcome.DiskFull
                        ? Strings.Format("Convert_DiskFullMessage", outcome.CreatedFileCount)
                        : Strings.Format("Convert_ErrorMessage", outcome.ErrorMessage, outcome.CreatedFileCount));
                    break;
            }
        }

        private void OpenResultFolder(string folder)
        {
            if (!OpenFolderAfter) return;

            try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Log(Strings.Format("Convert_OpenFolderFailedLog", ex.Message)); }
        }

        [RelayCommand]
        private void Cancel()
        {
            _pauseController?.Resume();
            _cts?.Cancel();
            StatusText = Strings.Get("Common_Cancelling");
        }

        [RelayCommand]
        private void TogglePause()
        {
            if (_pauseController == null || IsVerifying) return;

            if (IsPaused)
            {
                _pauseController.Resume();
                IsPaused = false;
                PauseButtonText = Strings.Get("Common_PauseButton");
                StatusText = Strings.Get("Common_Resumed");
                AppLogger.Log(Strings.Get("Convert_ResumedLog"));
            }
            else
            {
                _pauseController.Pause();
                IsPaused = true;
                PauseButtonText = Strings.Get("Common_ResumeButton");
                StatusText = Strings.Get("Common_Paused");
                AppLogger.Log(Strings.Get("Convert_PausedLog"));
            }
        }

    }
}
