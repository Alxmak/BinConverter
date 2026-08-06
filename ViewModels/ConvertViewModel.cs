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
        [ObservableProperty] private bool deleteSourceAfter;
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
            if (!File.Exists(SourcePath)) { StatusText = Strings.Get("Convert_SelectSourceFirst"); return; }
            if (string.IsNullOrWhiteSpace(OutputFolder)) { StatusText = Strings.Get("Convert_SpecifyOutputFolder"); return; }

            long limit = CurrentLimitBytes;
            if (limit <= 0) { StatusText = Strings.Get("Convert_InvalidPartSize"); return; }

            string baseName = string.IsNullOrWhiteSpace(BaseFileName) ? "emmc.bin" : BaseFileName.Trim();
            var sourceInfo = new FileInfo(SourcePath);
            string outFolder = OutputFolder;

            if (FileConflictHelper.AnyConflict(outFolder, baseName))
            {
                var choice = await DialogService.ShowConfirmAsync(
                    Strings.Get("Convert_ConflictTitle"),
                    Strings.Format("Convert_ConflictMessage", baseName),
                    Strings.Get("Common_OverwriteChoice"), Strings.Get("Convert_NewFolderNearby"), Strings.Get("Common_CancelChoice"));

                if (choice == DialogChoice.Close) return;
                if (choice == DialogChoice.Secondary)
                {
                    outFolder = FileConflictHelper.SuggestAlternativeFolder(outFolder);
                    OutputFolder = outFolder;
                    AppLogger.Log(Strings.Format("Convert_ConflictLog", outFolder));
                }
            }

            Directory.CreateDirectory(outFolder);

            if (CheckDiskSpace)
            {
                var spaceCheck = DiskSpaceHelper.CheckSpace(outFolder, sourceInfo.Length);
                if (!spaceCheck.HasEnoughSpace)
                {
                    await DialogService.ShowWarningAsync(Strings.Get("Convert_LowSpaceTitle"),
                        Strings.Format("Convert_LowSpaceMessage",
                            SizeFormatHelper.Format(sourceInfo.Length),
                            SizeFormatHelper.Format(spaceCheck.RequiredBytes),
                            SizeFormatHelper.Format(spaceCheck.AvailableBytes),
                            SizeFormatHelper.Format(spaceCheck.MissingBytes)));
                    return;
                }
            }

            _cts = new CancellationTokenSource();
            _pauseController = new PauseController();
            IsBusy = true;
            IsPaused = false;
            IsVerifying = false;
            OperationLockService.Instance.IsBusy = true;
            PauseButtonText = Strings.Get("Common_PauseButton");
            OverallProgress = 0; CurrentFileProgress = 0; ShaProgress = 0;
            StatusText = Strings.Get("Convert_Started");
            AppLogger.Log(Strings.Format("Convert_StartLog", SourcePath, outFolder, baseName, limit));

            var createdFiles = new List<string>();
            long totalWorkBytes = sourceInfo.Length * (VerifyHashAfter ? 2 : 1);

            var splitProgress = new Progress<SplitProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesWritten / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)p.TotalBytesWritten / totalWorkBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = overallPct;
                CurrentFileLabel = Strings.Format("Common_FileProgressLabel", p.CurrentFileName, p.CurrentFileIndex, p.TotalFiles);
                StatusText = Strings.Format("Convert_ProgressStatus", p.TotalBytesWritten, p.TotalBytes, filePct);
            });

            var hashProgress = new Progress<(long done, long total)>(p =>
            {
                if (!IsVerifying)
                {
                    IsVerifying = true;
                    if (IsPaused) { _pauseController?.Resume(); IsPaused = false; PauseButtonText = Strings.Get("Common_PauseButton"); }
                    StatusText = Strings.Get("Common_VerifyingHash");
                }
                ShaProgress = p.total > 0 ? (double)p.done / p.total * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)(sourceInfo.Length + p.done) / totalWorkBytes * 100.0 : 100.0;
                OverallProgress = Math.Min(100.0, overallPct);
            });

            using var bgScope = new BackgroundIoScope();

            try
            {
                var result = await Task.Run(() => FileSplitter.SplitAsync(
                    SourcePath, outFolder, baseName, limit, VerifyHashAfter,
                    splitProgress, AppLogger.Log, _cts.Token, createdFiles, hashProgress, _pauseController));

                AppLogger.Log(Strings.Format("Convert_FinishedLog", result.PartsCreated, result.TotalBytes));

                bool safeToDeleteSource = !result.VerifyPerformed || result.HashesMatch;

                if (result.VerifyPerformed)
                {
                    // Пункт 1: статус-строку не дублируем текстом успеха — итог и так виден в диалоге ниже.
                    StatusText = result.HashesMatch ? Strings.Get("Common_Done") : Strings.Get("Convert_HashCheckFailed");
                    if (result.HashesMatch)
                    {
                        await DialogService.ShowInfoAsync(Strings.Get("Convert_DoneTitle"),
                            Strings.Format("Convert_DoneVerifiedMessage", result.PartsCreated, result.SourceHash));
                    }
                    else
                    {
                        await DialogService.ShowErrorAsync(Strings.Get("Convert_VerifyErrorTitle"),
                            Strings.Format("Convert_VerifyErrorMessage", result.SourceHash, result.RecombinedHash));
                    }
                }
                else
                {
                    StatusText = Strings.Get("Common_Done");
                    await DialogService.ShowInfoAsync(Strings.Get("Convert_DoneTitle"), Strings.Format("Convert_DoneMessage", result.PartsCreated));
                }

                if (DeleteSourceAfter && safeToDeleteSource)
                {
                    try
                    {
                        File.Delete(SourcePath);
                        AppLogger.Log(Strings.Format("Convert_SourceDeletedLog", SourcePath));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log(Strings.Format("Convert_SourceDeleteFailedLog", ex.Message));
                        await DialogService.ShowWarningAsync(Strings.Get("Common_Error"), Strings.Format("Convert_SourceDeleteFailedMessage", ex.Message));
                    }
                }
                else if (DeleteSourceAfter && !safeToDeleteSource)
                {
                    AppLogger.Log(Strings.Get("Convert_SourceKeptLog"));
                }

                if (OpenFolderAfter)
                {
                    try { Process.Start(new ProcessStartInfo { FileName = outFolder, UseShellExecute = true }); }
                    catch (Exception ex) { AppLogger.Log(Strings.Format("Convert_OpenFolderFailedLog", ex.Message)); }
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = Strings.Get("Convert_CancellingStatus");
                AppLogger.Log(Strings.Get("Convert_CancelledLog"));
                CleanupCreatedFiles(createdFiles);
                StatusText = Strings.Get("Convert_CancelledStatus");
                await DialogService.ShowInfoAsync(Strings.Get("Convert_CancelledTitle"), Strings.Format("Convert_CancelledMessage", createdFiles.Count));
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Convert_ErrorLog", ex.Message));
                CleanupCreatedFiles(createdFiles);
                StatusText = Strings.Get("Convert_ErrorStatus");

                string message = IsDiskFullError(ex)
                    ? Strings.Format("Convert_DiskFullMessage", createdFiles.Count)
                    : Strings.Format("Convert_ErrorMessage", ex.Message, createdFiles.Count);

                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), message);
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

        private static void CleanupCreatedFiles(List<string> createdFiles)
        {
            if (createdFiles.Count == 0) return;
            AppLogger.Log(Strings.Format("Convert_DeletingIncompleteLog", createdFiles.Count));
            foreach (var path in createdFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        AppLogger.Log(Strings.Format("Convert_DeletedLog", Path.GetFileName(path)));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log(Strings.Format("Convert_DeleteFailedLog", Path.GetFileName(path), ex.Message));
                }
            }
        }

        private static bool IsDiskFullError(Exception ex)
        {
            int code = ex.HResult & 0xFFFF;
            return ex is IOException && (code == 0x27 || code == 0x70);
        }
    }
}
