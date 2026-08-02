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
using BinConverter.Core;
using BinConverter.Models;
using BinConverter.Services;

namespace BinConverter.ViewModels
{
    public partial class ConvertViewModel : ObservableObject
    {
        private const int CollapsedFileListCount = 4;
        private const int MaxFilesToEnumerate = 2000;

        public ObservableCollection<ProgrammerPreset> Presets { get; } = new()
        {
            new ProgrammerPreset("TNM5000", FileSplitter.DefaultMaxPartSizeBytes),
            new ProgrammerPreset("RT809H", FileSplitter.DefaultMaxPartSizeBytes),
            new ProgrammerPreset("Пользовательские настройки", null)
        };

        // Пункт 4/14: журнал общий на всё приложение — просто проброс, не своя коллекция.
        public ObservableCollection<string> LogLines => LogService.Lines;

        [ObservableProperty] private ProgrammerPreset selectedPreset = null!;

        [ObservableProperty] private string sourcePath = "";
        [ObservableProperty] private string outputFolder = "";
        [ObservableProperty] private string baseFileName = "emmc.bin";

        [ObservableProperty] private string customLimitBytesText = FileSplitter.DefaultMaxPartSizeBytes.ToString();

        [ObservableProperty] private string generalInfoText = "";
        [ObservableProperty] private string displayedFilesText = "";
        [ObservableProperty] private bool showExpandButton;
        [ObservableProperty] private bool showAllFiles;
        [ObservableProperty] private string expandButtonText = "Показать все файлы";

        [ObservableProperty] private bool verifyHashAfter = true;
        [ObservableProperty] private bool openFolderAfter = true;
        [ObservableProperty] private bool deleteSourceAfter;
        [ObservableProperty] private bool checkDiskSpace = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private bool isBusy;

        [ObservableProperty] private bool isPaused;
        [ObservableProperty] private string pauseButtonText = "⏸ Приостановить";
        [ObservableProperty] private bool isVerifying;

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;
        [ObservableProperty] private double shaProgress;
        [ObservableProperty] private string statusText = "Готово";

        public bool CanStart => !IsBusy;
        public bool CanPause => IsBusy && !IsVerifying;
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

        private static string GetDefaultOutputFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BinConverter", "New firmware files");

        private long CurrentLimitBytes =>
            SelectedPreset?.MaxPartSizeBytes
            ?? (long.TryParse(CustomLimitBytesText.Replace(" ", ""), out var bytes) && bytes > 0 ? bytes : 0);

        partial void OnSelectedPresetChanged(ProgrammerPreset value)
        {
            OnPropertyChanged(nameof(IsCustomPreset));
            if (value?.MaxPartSizeBytes is long fixedBytes)
                CustomLimitBytesText = fixedBytes.ToString();
            UpdatePreview();
        }

        partial void OnCustomLimitBytesTextChanged(string value) => UpdatePreview();
        partial void OnShowAllFilesChanged(bool value) => RebuildFilesText();
        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanPause));
        }
        partial void OnIsVerifyingChanged(bool value) => OnPropertyChanged(nameof(CanPause));

        [RelayCommand]
        private void BrowseSource()
        {
            var dlg = new OpenFileDialog { Filter = "BIN файлы (*.bin)|*.bin|Все файлы (*.*)|*.*" };
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
            UpdatePreview();
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new OpenFolderDialog { Title = "Выберите папку вывода" };
            if (dlg.ShowDialog() == true)
            {
                OutputFolder = dlg.FolderName;
                UpdatePreview();
            }
        }

        [RelayCommand]
        private void CopyOutputPath()
        {
            try { Clipboard.SetText(OutputFolder); }
            catch { /* буфер обмена иногда занят другим процессом — не критично */ }
        }

        [RelayCommand]
        private void ToggleExpand() => ShowAllFiles = !ShowAllFiles;

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

        // ============================= Предпросмотр =============================

        private void UpdatePreview()
        {
            long limit = CurrentLimitBytes;

            if (!File.Exists(SourcePath) || limit <= 0)
            {
                GeneralInfoText = "";
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = "";
                return;
            }

            long size = new FileInfo(SourcePath).Length;

            if (limit < 1024)
            {
                GeneralInfoText = "Введите корректный размер части (минимум 1024 байта)";
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = "";
                return;
            }

            int count = FileSplitter.CalculateExpectedPartCount(size, limit);

            if (count > MaxFilesToEnumerate)
            {
                GeneralInfoText =
                    $"Размер исходного файла: {SizeFormatHelper.Format(size)}\n" +
                    $"Ожидаемое количество файлов: {count:N0} (слишком много для отображения по отдельности)";
                _expectedFileCount = count;
                ShowExpandButton = false;
                DisplayedFilesText = "";
                return;
            }

            _expectedFileCount = count;
            _expectedTotalSize = size;
            _expectedLimitBytes = limit;

            GeneralInfoText =
                $"Размер исходного файла: {SizeFormatHelper.Format(size)}\n" +
                $"Ожидаемое количество файлов: {count}";

            ShowExpandButton = count > CollapsedFileListCount;
            ShowAllFiles = false;
            RebuildFilesText();
        }

        private void RebuildFilesText()
        {
            if (_expectedFileCount == 0) { DisplayedFilesText = ""; return; }

            int toShow = ShowAllFiles ? _expectedFileCount : Math.Min(CollapsedFileListCount, _expectedFileCount);

            var sb = new StringBuilder();
            sb.AppendLine("Размер каждого файла:");
            long remaining = _expectedTotalSize;
            for (int i = 1; i <= _expectedFileCount; i++)
            {
                long partSize = Math.Min(_expectedLimitBytes, remaining);
                remaining -= partSize;
                if (i > toShow) continue;
                sb.AppendLine($"Файл {i}: {SizeFormatHelper.Format(partSize)}");
            }

            if (!ShowAllFiles && _expectedFileCount > CollapsedFileListCount)
                sb.AppendLine($"… и ещё {_expectedFileCount - CollapsedFileListCount} файл(ов)");

            ExpandButtonText = ShowAllFiles ? "Свернуть список" : $"Показать все {_expectedFileCount} файлов";
            DisplayedFilesText = sb.ToString().TrimEnd();
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            if (!File.Exists(SourcePath)) { StatusText = "Выберите существующий исходный файл."; return; }
            if (string.IsNullOrWhiteSpace(OutputFolder)) { StatusText = "Укажите папку вывода."; return; }

            long limit = CurrentLimitBytes;
            if (limit <= 0) { StatusText = "Некорректный размер части."; return; }

            string baseName = string.IsNullOrWhiteSpace(BaseFileName) ? "emmc.bin" : BaseFileName.Trim();
            var sourceInfo = new FileInfo(SourcePath);
            string outFolder = OutputFolder;

            if (FileConflictHelper.AnyConflict(outFolder, baseName))
            {
                var choice = await DialogService.ShowConfirmAsync(
                    "Файлы уже существуют",
                    $"В папке уже есть файлы с именем «{baseName}» (или его частей).",
                    "Перезаписать", "Новая папка рядом", "Отмена");

                if (choice == DialogChoice.Close) return;
                if (choice == DialogChoice.Secondary)
                {
                    outFolder = FileConflictHelper.SuggestAlternativeFolder(outFolder);
                    OutputFolder = outFolder;
                    AppLogger.Log($"Обнаружен конфликт имён — используется новая папка: {outFolder}");
                }
            }

            Directory.CreateDirectory(outFolder);

            if (CheckDiskSpace)
            {
                var spaceCheck = DiskSpaceHelper.CheckSpace(outFolder, sourceInfo.Length);
                if (!spaceCheck.HasEnoughSpace)
                {
                    await DialogService.ShowWarningAsync("Недостаточно места",
                        $"Размер исходного файла: {SizeFormatHelper.Format(sourceInfo.Length)}\n" +
                        $"Необходимо (с запасом 5%): {SizeFormatHelper.Format(spaceCheck.RequiredBytes)}\n" +
                        $"Свободно: {SizeFormatHelper.Format(spaceCheck.AvailableBytes)}\n" +
                        $"Не хватает: {SizeFormatHelper.Format(spaceCheck.MissingBytes)}");
                    return;
                }
            }

            _cts = new CancellationTokenSource();
            _pauseController = new PauseController();
            IsBusy = true;
            IsPaused = false;
            IsVerifying = false;
            OperationLockService.Instance.IsBusy = true;
            PauseButtonText = "⏸ Приостановить";
            OverallProgress = 0; CurrentFileProgress = 0; ShaProgress = 0;
            StatusText = "Конвертирование начато...";
            AppLogger.Log($"=== Начало конвертирования: {SourcePath} -> {outFolder}\\{baseName} (макс. {limit:N0} байт/файл) ===");

            var createdFiles = new List<string>();
            long totalWorkBytes = sourceInfo.Length * (VerifyHashAfter ? 2 : 1);

            var splitProgress = new Progress<SplitProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesWritten / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)p.TotalBytesWritten / totalWorkBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = overallPct;
                CurrentFileLabel = $"{p.CurrentFileName} — файл {p.CurrentFileIndex} из {p.TotalFiles}";
                StatusText = $"{p.TotalBytesWritten:N0} / {p.TotalBytes:N0} байт  ({filePct:F0}% текущего файла)";
            });

            var hashProgress = new Progress<(long done, long total)>(p =>
            {
                if (!IsVerifying)
                {
                    IsVerifying = true;
                    if (IsPaused) { _pauseController?.Resume(); IsPaused = false; PauseButtonText = "⏸ Приостановить"; }
                    StatusText = "Проверка SHA-256...";
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

                AppLogger.Log($"=== Конвертирование завершено. Файлов: {result.PartsCreated}, всего байт: {result.TotalBytes:N0} ===");

                bool safeToDeleteSource = !result.VerifyPerformed || result.HashesMatch;

                if (result.VerifyPerformed)
                {
                    // Пункт 1: статус-строку не дублируем текстом успеха — итог и так виден в диалоге ниже.
                    StatusText = result.HashesMatch ? "Готово." : "Ошибка проверки SHA-256.";
                    if (result.HashesMatch)
                    {
                        await DialogService.ShowInfoAsync("Готово",
                            $"Конвертирование завершено успешно.\nСоздано файлов: {result.PartsCreated}\n\nSHA-256 совпадает — данные не повреждены.\n\n{result.SourceHash}");
                    }
                    else
                    {
                        await DialogService.ShowErrorAsync("Ошибка проверки",
                            $"ВНИМАНИЕ: контрольные суммы не совпадают!\n\nИсточник: {result.SourceHash}\nРезультат: {result.RecombinedHash}");
                    }
                }
                else
                {
                    StatusText = "Готово.";
                    await DialogService.ShowInfoAsync("Готово", $"Конвертирование завершено.\nСоздано файлов: {result.PartsCreated}");
                }

                if (DeleteSourceAfter && safeToDeleteSource)
                {
                    try
                    {
                        File.Delete(SourcePath);
                        AppLogger.Log($"Исходный файл удалён: {SourcePath}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Не удалось удалить исходный файл: {ex.Message}");
                        await DialogService.ShowWarningAsync("Ошибка", $"Не удалось удалить исходный файл:\n{ex.Message}");
                    }
                }
                else if (DeleteSourceAfter && !safeToDeleteSource)
                {
                    AppLogger.Log("Исходный файл НЕ удалён — проверка SHA-256 не пройдена.");
                }

                if (OpenFolderAfter)
                {
                    try { Process.Start(new ProcessStartInfo { FileName = outFolder, UseShellExecute = true }); }
                    catch (Exception ex) { AppLogger.Log($"Не удалось открыть папку: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Отмена — удаление незавершённых файлов...";
                AppLogger.Log("=== Конвертирование отменено пользователем ===");
                CleanupCreatedFiles(createdFiles);
                StatusText = "Отменено. Незавершённые файлы удалены.";
                await DialogService.ShowInfoAsync("Отменено", $"Конвертирование отменено.\nУдалено незавершённых файлов: {createdFiles.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"ОШИБКА: {ex.Message}");
                CleanupCreatedFiles(createdFiles);
                StatusText = "Ошибка. Незавершённые файлы удалены.";

                string message = IsDiskFullError(ex)
                    ? $"На диске закончилось свободное место во время записи.\n\nОперация прервана, незавершённые файлы ({createdFiles.Count}) удалены — освободите место и попробуйте снова."
                    : $"Ошибка при конвертировании:\n{ex.Message}\n\nНезавершённые файлы ({createdFiles.Count}) удалены.";

                await DialogService.ShowErrorAsync("Ошибка", message);
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
            StatusText = "Отмена...";
        }

        [RelayCommand]
        private void TogglePause()
        {
            if (_pauseController == null || IsVerifying) return;

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
