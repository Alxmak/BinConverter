using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class MergeViewModel : ObservableObject
    {
        public ObservableCollection<string> LogLines => LogService.Lines;

        [ObservableProperty] private string sourcePath = "";
        [ObservableProperty] private string chainInfoText = Strings.Get("Merge_DragHint");
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
        [ObservableProperty] private string statusText = Strings.Get("Common_Ready");

        public bool CanStart => !IsBusy;

        private CancellationTokenSource? _cts;
        private PauseController? _pauseController;

        public void Detach() { }

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

        // Пункт: поле пути теперь редактируется свободно (можно вставлять и печатать) —
        // цепочка частей должна пересчитываться и при прямом вводе, не только через SetSource.
        partial void OnSourcePathChanged(string value) => TryResolveChain();

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
                string basePath = HashHelper.ResolveBasePath(SourcePath);
                var chain = HashHelper.FindPartChain(basePath);
                long total = 0;
                foreach (var f in chain) total += new FileInfo(f).Length;

                ChainInfoText = Strings.Format("Merge_ChainFoundInfo", chain.Count, SizeFormatHelper.Format(total), Path.GetFileName(chain[0]));

                if (string.IsNullOrWhiteSpace(OutputPath))
                {
                    string name = Path.GetFileNameWithoutExtension(basePath) + "_merged" + Path.GetExtension(basePath);
                    OutputPath = Path.Combine(OutputPathSettingsService.GetMergeFolder(), name);
                }
            }
            catch (Exception ex)
            {
                ChainInfoText = Strings.Format("Merge_ChainResolveError", ex.Message);
            }
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new SaveFileDialog { Filter = Strings.Get("Common_BinFileFilter"), FileName = "emmc_merged.bin" };
            if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
        }

        // ============================= Журнал =============================

        [RelayCommand]
        private void OpenLog() => AppLogger.OpenLogFile();

        [RelayCommand]
        private void SaveLog()
        {
            var dlg = new SaveFileDialog { Filter = Strings.Get("Common_TextFileFilter"), FileName = "TweakFirmware.log.txt" };
            if (dlg.ShowDialog() == true)
            {
                try { LogService.SaveAs(dlg.FileName); }
                catch (Exception ex) { MessageBox.Show(Strings.Format("Common_SaveLogFailed", ex.Message), Strings.Get("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        [RelayCommand]
        private void ClearLog() => LogService.Clear();

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            if (!File.Exists(SourcePath)) { StatusText = Strings.Get("Merge_SelectSourceFirst"); return; }
            if (string.IsNullOrWhiteSpace(OutputPath)) { StatusText = Strings.Get("Merge_SpecifyOutputFile"); return; }

            string output = OutputPath;

            if (File.Exists(output))
            {
                var choice = await DialogService.ShowConfirmAsync(
                    Strings.Get("Merge_FileExistsTitle"),
                    Strings.Format("Merge_FileExistsMessage", Path.GetFileName(output)),
                    Strings.Get("Common_OverwriteChoice"), Strings.Get("Merge_NewNameNearby"), Strings.Get("Common_CancelChoice"));

                if (choice == DialogChoice.Close) return;
                if (choice == DialogChoice.Secondary)
                {
                    output = FileConflictHelper.SuggestAlternativeFilePath(output);
                    OutputPath = output;
                    AppLogger.Log(Strings.Format("Merge_ConflictLog", output));
                }
            }

            List<string> chainForSpace;
            long neededBytes;
            try
            {
                string basePathForSpace = HashHelper.ResolveBasePath(SourcePath);
                chainForSpace = HashHelper.FindPartChain(basePathForSpace);
                neededBytes = 0;
                foreach (var f in chainForSpace) neededBytes += new FileInfo(f).Length;
            }
            catch (Exception ex)
            {
                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), Strings.Format("Merge_ChainCheckErrorMessage", ex.Message));
                return;
            }

            string outDir = Path.GetDirectoryName(output) ?? ".";
            Directory.CreateDirectory(outDir);
            var spaceCheck = DiskSpaceHelper.CheckSpace(outDir, neededBytes);
            if (!spaceCheck.HasEnoughSpace)
            {
                await DialogService.ShowWarningAsync(Strings.Get("Convert_LowSpaceTitle"),
                    Strings.Format("Merge_LowSpaceMessage",
                        SizeFormatHelper.Format(neededBytes),
                        SizeFormatHelper.Format(spaceCheck.RequiredBytes),
                        SizeFormatHelper.Format(spaceCheck.AvailableBytes),
                        SizeFormatHelper.Format(spaceCheck.MissingBytes)));
                return;
            }

            _cts = new CancellationTokenSource();
            _pauseController = new PauseController();
            IsBusy = true;
            IsPaused = false;
            OperationLockService.Instance.IsBusy = true;
            PauseButtonText = Strings.Get("Common_PauseButton");
            OverallProgress = 0; CurrentFileProgress = 0;
            StatusText = Strings.Get("Merge_Started");
            AppLogger.Log(Strings.Format("Merge_StartLog", SourcePath, output));

            var createdFiles = new List<string>();

            var progress = new Progress<MergeProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesRead / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double totalPct = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = totalPct;
                CurrentFileLabel = Strings.Format("Common_FileProgressLabel", p.CurrentFileName, p.CurrentFileIndex, p.TotalFiles);
                StatusText = Strings.Format("Merge_ProgressStatus", p.TotalBytesProcessed, p.TotalBytes, totalPct);
            });

            using var bgScope = new BackgroundIoScope();

            try
            {
                var result = await Task.Run(() => FileMerger.MergeAsync(SourcePath, output, progress, AppLogger.Log, _cts.Token, createdFiles, _pauseController));

                AppLogger.Log(Strings.Format("Merge_FinishedLog", result.PartsUsed, result.TotalBytes));
                AppLogger.Log(Strings.Format("Merge_ResultHashLog", result.MergedHash));

                string expected = ExpectedHashText.Trim();
                if (!string.IsNullOrEmpty(expected))
                {
                    bool match = string.Equals(expected, result.MergedHash, StringComparison.OrdinalIgnoreCase);
                    AppLogger.Log(match ? Strings.Get("Merge_HashMatchLog") : Strings.Get("Merge_HashMismatchLog"));
                    StatusText = match ? Strings.Get("Common_Done") : Strings.Get("Merge_HashCheckFailedStatus");

                    if (match)
                        await DialogService.ShowInfoAsync(Strings.Get("Merge_ResultTitle"), Strings.Format("Merge_ResultMatchMessage", result.MergedHash));
                    else
                        await DialogService.ShowErrorAsync(Strings.Get("Merge_ResultTitle"), Strings.Format("Merge_ResultMismatchMessage", expected, result.MergedHash));
                }
                else
                {
                    StatusText = Strings.Get("Common_Done");
                    await DialogService.ShowInfoAsync(Strings.Get("Convert_DoneTitle"), Strings.Format("Merge_DoneMessage", result.PartsUsed, result.TotalBytes, result.MergedHash));
                }

                if (OpenFolderAfter)
                {
                    string? resultDir = Path.GetDirectoryName(result.OutputPath);
                    if (!string.IsNullOrEmpty(resultDir))
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = resultDir, UseShellExecute = true }); }
                        catch (Exception ex) { AppLogger.Log(Strings.Format("Convert_OpenFolderFailedLog", ex.Message)); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = Strings.Get("Merge_CancellingStatus");
                AppLogger.Log(Strings.Get("Merge_CancelledLog"));
                CleanupCreatedFiles(createdFiles);
                StatusText = Strings.Get("Merge_CancelledStatus");
                await DialogService.ShowInfoAsync(Strings.Get("Convert_CancelledTitle"), Strings.Get("Merge_CancelledMessage"));
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Convert_ErrorLog", ex.Message));
                CleanupCreatedFiles(createdFiles);
                StatusText = Strings.Get("Merge_ErrorStatus");

                string message = IsDiskFullError(ex)
                    ? Strings.Get("Merge_DiskFullMessage")
                    : Strings.Format("Merge_ErrorMessage", ex.Message);

                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), message);
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
            if (_pauseController == null) return;

            if (IsPaused)
            {
                _pauseController.Resume();
                IsPaused = false;
                PauseButtonText = Strings.Get("Common_PauseButton");
                StatusText = Strings.Get("Common_Resumed");
                AppLogger.Log(Strings.Get("Merge_ResumedLog"));
            }
            else
            {
                _pauseController.Pause();
                IsPaused = true;
                PauseButtonText = Strings.Get("Common_ResumeButton");
                StatusText = Strings.Get("Common_Paused");
                AppLogger.Log(Strings.Get("Merge_PausedLog"));
            }
        }

        private static void CleanupCreatedFiles(List<string> createdFiles)
        {
            if (createdFiles.Count == 0) return;
            AppLogger.Log(Strings.Format("Merge_DeletingIncompleteLog", createdFiles.Count));
            foreach (var path in createdFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        AppLogger.Log(Strings.Format("Merge_DeletedLog", Path.GetFileName(path)));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log(Strings.Format("Merge_DeleteFailedLog", Path.GetFileName(path), ex.Message));
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
