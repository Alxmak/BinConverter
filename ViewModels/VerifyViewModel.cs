using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using BinConverter.Core;
using BinConverter.Services;

namespace BinConverter.ViewModels
{
    public partial class VerifyViewModel : ObservableObject
    {
        public ObservableCollection<string> LogLines => LogService.Lines;

        [ObservableProperty] private string fileAPath = "";
        [ObservableProperty] private string fileBPath = "";
        [ObservableProperty] private string hashAText = "";
        [ObservableProperty] private string hashBText = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
        private bool isBusy;

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private string statusText = "Готово";

        [ObservableProperty] private bool hasResult;
        [ObservableProperty] private bool isMatch;
        [ObservableProperty] private string resultText = "";

        public bool CanCompare => !IsBusy;

        private CancellationTokenSource? _cts;

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCompare));

        [RelayCommand]
        private void BrowseA()
        {
            var dlg = new OpenFileDialog { Filter = "Все файлы (*.*)|*.*" };
            if (dlg.ShowDialog() == true) FileAPath = dlg.FileName;
        }

        [RelayCommand]
        private void BrowseB()
        {
            var dlg = new OpenFileDialog { Filter = "Все файлы (*.*)|*.*" };
            if (dlg.ShowDialog() == true) FileBPath = dlg.FileName;
        }

        public void SetFileA(string path)
        {
            if (File.Exists(path)) FileAPath = path;
        }

        public void SetFileB(string path)
        {
            if (File.Exists(path)) FileBPath = path;
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
                catch (Exception ex) { _ = DialogService.ShowErrorAsync("Ошибка", $"Не удалось сохранить лог:\n{ex.Message}"); }
            }
        }

        [RelayCommand]
        private void ClearLog() => LogService.Clear();

        [RelayCommand(CanExecute = nameof(CanCompare))]
        private async Task CompareAsync()
        {
            if (!File.Exists(FileAPath) || !File.Exists(FileBPath))
            {
                await DialogService.ShowWarningAsync("Ошибка", "Выберите оба файла.");
                return;
            }

            _cts = new CancellationTokenSource();
            IsBusy = true;
            HasResult = false;
            OverallProgress = 0;
            AppLogger.Log($"=== Сравнение хэшей: {FileAPath}  vs  {FileBPath} ===");

            try
            {
                long sizeA = new FileInfo(FileAPath).Length;
                long sizeB = new FileInfo(FileBPath).Length;
                long totalWork = sizeA + sizeB;

                CurrentFileLabel = $"Файл A — {Path.GetFileName(FileAPath)}";
                var progressA = new Progress<(long done, long total)>(p =>
                {
                    OverallProgress = totalWork > 0 ? (double)p.done / totalWork * 100.0 : 0;
                });
                HashAText = await Task.Run(() => HashHelper.ComputeFileHashAsync(FileAPath, _cts.Token, progressA));

                CurrentFileLabel = $"Файл B — {Path.GetFileName(FileBPath)}";
                var progressB = new Progress<(long done, long total)>(p =>
                {
                    OverallProgress = totalWork > 0 ? (double)(sizeA + p.done) / totalWork * 100.0 : 0;
                });
                HashBText = await Task.Run(() => HashHelper.ComputeFileHashAsync(FileBPath, _cts.Token, progressB));

                bool match = string.Equals(HashAText, HashBText, StringComparison.OrdinalIgnoreCase);
                AppLogger.Log(match ? "Хэши совпадают ✓" : "Хэши НЕ совпадают");

                IsMatch = match;
                ResultText = match ? "✓ Хэши совпадают — данные идентичны" : "✕ Хэши НЕ совпадают — файлы различаются";
                HasResult = true;
                StatusText = "Готово.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Отменено.";
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка.";
                await DialogService.ShowErrorAsync("Ошибка", $"Ошибка при вычислении хэша:\n{ex.Message}");
            }
            finally
            {
                OverallProgress = 0;
                CurrentFileLabel = "";
                IsBusy = false;
                _cts = null;
            }
        }

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();
    }
}
