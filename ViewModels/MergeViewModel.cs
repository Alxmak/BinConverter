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
using BinConverter.Core;
using BinConverter.Services;

namespace BinConverter.ViewModels
{
    public partial class MergeViewModel : ObservableObject
    {
        public ObservableCollection<string> LogLines => LogService.Lines;

        [ObservableProperty] private string sourcePath = "";
        [ObservableProperty] private string chainInfoText = "Перетащите любой файл цепочки сюда, или выберите через «Обзор…»";
        [ObservableProperty] private string outputPath = "";
        [ObservableProperty] private string expectedHashText = "";

        [ObservableProperty] private bool openFolderAfter = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private bool isBusy;

        [ObservableProperty] private bool isPaused;
        [ObservableProperty] private string pauseButtonText = "⏸ Приостановить";

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;
        [ObservableProperty] private string statusText = "Готово";

        public bool CanStart => !IsBusy;

        private CancellationTokenSource? _cts;
        private PauseController? _pauseController;

        public void Detach() { }

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

        [RelayCommand]
        private void BrowseSource()
        {
            var dlg = new OpenFileDialog { Filter = "BIN и части (*.bin;*.part*)|*.bin;*.bin.part*|Все файлы (*.*)|*.*" };
            if (dlg.ShowDialog() == true) SetSource(dlg.FileName);
        }

        public void SetSource(string path)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show("Файл не найден.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SourcePath = path;
            try
            {
                string basePath = HashHelper.ResolveBasePath(path);
                var chain = HashHelper.FindPartChain(basePath);
                long total = 0;
                foreach (var f in chain) total += new FileInfo(f).Length;

                ChainInfoText = $"Найдено частей: {chain.Count} ({SizeFormatHelper.Format(total)}). Первая: {Path.GetFileName(chain[0])}";

                if (string.IsNullOrWhiteSpace(OutputPath))
                {
                    string dir = Path.GetDirectoryName(basePath) ?? "";
                    string name = Path.GetFileNameWithoutExtension(basePath) + "_merged" + Path.GetExtension(basePath);
                    OutputPath = Path.Combine(dir, name);
                }
            }
            catch (Exception ex)
            {
                ChainInfoText = $"Не удалось определить цепочку: {ex.Message}";
            }
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new SaveFileDialog { Filter = "BIN файлы (*.bin)|*.bin|Все файлы (*.*)|*.*", FileName = "emmc_merged.bin" };
            if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
        }

        // ============================= Журнал =============================

        [RelayCommand]
        private void OpenLog() => AppLogger.OpenLogFile();

        [RelayCommand]
        private void SaveLog()
        {
            var dlg = new SaveFileDialog { Filter = "Текстовый файл (*.txt)|*.txt", FileName = "BinConverter.log.txt" };
            if (dlg.ShowDialog() == true)
            {
                try { LogService.SaveAs(dlg.FileName); }
                catch (Exception ex) { MessageBox.Show($"Не удалось сохранить лог:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        [RelayCommand]
        private void ClearLog() => LogService.Clear();

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            if (!File.Exists(SourcePath)) { StatusText = "Выберите файл из цепочки частей."; return; }
            if (string.IsNullOrWhiteSpace(OutputPath)) { StatusText = "Укажите файл результата."; return; }

            string output = OutputPath;

            if (File.Exists(output))
            {
                var choice = await DialogService.ShowConfirmAsync(
                    "Файл уже существует",
                    $"Файл «{Path.GetFileName(output)}» уже существует.",
                    "Перезаписать", "Новое имя рядом", "Отмена");

                if (choice == DialogChoice.Close) return;
                if (choice == DialogChoice.Secondary)
                {
                    output = FileConflictHelper.SuggestAlternativeFilePath(output);
                    OutputPath = output;
                    AppLogger.Log($"Обнаружен конфликт имени — используется новый файл: {output}");
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
                await DialogService.ShowErrorAsync("Ошибка", $"Не удалось проверить цепочку частей:\n{ex.Message}");
                return;
            }

            string outDir = Path.GetDirectoryName(output) ?? ".";
            Directory.CreateDirectory(outDir);
            var spaceCheck = DiskSpaceHelper.CheckSpace(outDir, neededBytes);
            if (!spaceCheck.HasEnoughSpace)
            {
                await DialogService.ShowWarningAsync("Недостаточно места",
                    $"Размер собираемых данных: {SizeFormatHelper.Format(neededBytes)}\n" +
                    $"Необходимо (с запасом 5%): {SizeFormatHelper.Format(spaceCheck.RequiredBytes)}\n" +
                    $"Свободно: {SizeFormatHelper.Format(spaceCheck.AvailableBytes)}\n" +
                    $"Не хватает: {SizeFormatHelper.Format(spaceCheck.MissingBytes)}");
                return;
            }

            _cts = new CancellationTokenSource();
            _pauseController = new PauseController();
            IsBusy = true;
            IsPaused = false;
            OperationLockService.Instance.IsBusy = true;
            PauseButtonText = "⏸ Приостановить";
            OverallProgress = 0; CurrentFileProgress = 0;
            StatusText = "Сборка начата...";
            AppLogger.Log($"=== Начало сборки: {SourcePath} -> {output} ===");

            var createdFiles = new List<string>();

            var progress = new Progress<MergeProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesRead / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double totalPct = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = totalPct;
                CurrentFileLabel = $"{p.CurrentFileName} — файл {p.CurrentFileIndex} из {p.TotalFiles}";
                StatusText = $"{p.TotalBytesProcessed:N0} / {p.TotalBytes:N0} байт  ({totalPct:F1}%)";
            });

            using var bgScope = new BackgroundIoScope();

            try
            {
                var result = await Task.Run(() => FileMerger.MergeAsync(SourcePath, output, progress, AppLogger.Log, _cts.Token, createdFiles, _pauseController));

                AppLogger.Log($"=== Сборка завершена. Частей: {result.PartsUsed}, байт: {result.TotalBytes:N0} ===");
                AppLogger.Log($"SHA-256 результата: {result.MergedHash}");

                string expected = ExpectedHashText.Trim();
                if (!string.IsNullOrEmpty(expected))
                {
                    bool match = string.Equals(expected, result.MergedHash, StringComparison.OrdinalIgnoreCase);
                    AppLogger.Log(match ? "Сверка с указанным хэшем: совпадает ✓" : "Сверка с указанным хэшем: НЕ совпадает!");
                    StatusText = match ? "Готово." : "Ошибка сверки хэша.";

                    if (match)
                        await DialogService.ShowInfoAsync("Результат сборки", $"Сборка завершена.\nSHA-256 совпадает с указанным.\n\n{result.MergedHash}");
                    else
                        await DialogService.ShowErrorAsync("Результат сборки", $"ВНИМАНИЕ: SHA-256 НЕ совпадает!\n\nОжидался: {expected}\nПолучен: {result.MergedHash}");
                }
                else
                {
                    StatusText = "Готово.";
                    await DialogService.ShowInfoAsync("Готово", $"Сборка завершена.\nЧастей: {result.PartsUsed}\nРазмер: {result.TotalBytes:N0} байт\n\nSHA-256: {result.MergedHash}");
                }

                if (OpenFolderAfter)
                {
                    string? resultDir = Path.GetDirectoryName(result.OutputPath);
                    if (!string.IsNullOrEmpty(resultDir))
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = resultDir, UseShellExecute = true }); }
                        catch (Exception ex) { AppLogger.Log($"Не удалось открыть папку: {ex.Message}"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Отмена — удаление незавершённого файла...";
                AppLogger.Log("=== Сборка отменена пользователем ===");
                CleanupCreatedFiles(createdFiles);
                StatusText = "Отменено. Незавершённый файл удалён.";
                await DialogService.ShowInfoAsync("Отменено", "Сборка отменена.\nНезавершённый файл результата удалён.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"ОШИБКА: {ex.Message}");
                CleanupCreatedFiles(createdFiles);
                StatusText = "Ошибка. Незавершённый файл удалён.";

                string message = IsDiskFullError(ex)
                    ? "На диске закончилось свободное место во время записи.\n\nОперация прервана, незавершённый файл результата удалён — освободите место и попробуйте снова."
                    : $"Ошибка при сборке:\n{ex.Message}\n\nНезавершённый файл результата удалён.";

                await DialogService.ShowErrorAsync("Ошибка", message);
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
            StatusText = "Отмена...";
        }

        [RelayCommand]
        private void TogglePause()
        {
            if (_pauseController == null) return;

            if (IsPaused)
            {
                _pauseController.Resume();
                IsPaused = false;
                PauseButtonText = "⏸ Приостановить";
                StatusText = "Возобновлено.";
                AppLogger.Log("Операция возобновлена пользователем");
            }
            else
            {
                _pauseController.Pause();
                IsPaused = true;
                PauseButtonText = "▶ Возобновить";
                StatusText = "На паузе.";
                AppLogger.Log("Операция приостановлена пользователем");
            }
        }

        private static void CleanupCreatedFiles(List<string> createdFiles)
        {
            if (createdFiles.Count == 0) return;
            AppLogger.Log($"Удаление незавершённых файлов ({createdFiles.Count})...");
            foreach (var path in createdFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        AppLogger.Log($"Удалён: {Path.GetFileName(path)}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Не удалось удалить {Path.GetFileName(path)}: {ex.Message}");
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
