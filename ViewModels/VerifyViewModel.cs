using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class VerifyViewModel : LogHostViewModel
    {
        [ObservableProperty] private string fileAPath = "";
        [ObservableProperty] private string fileBPath = "";
        // Карточка "Результат" видна всегда, поэтому до первого сравнения в ней стоят
        // прочерки и пояснение, а не пустые строки (NoValuePlaceholder — из LogHostViewModel).

        [ObservableProperty] private string hashAText = NoValuePlaceholder;
        [ObservableProperty] private string hashBText = NoValuePlaceholder;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
        private bool isBusy;

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private string statusText = Strings.Get("Common_Ready");

        [ObservableProperty] private bool hasResult;
        [ObservableProperty] private bool isMatch;
        [ObservableProperty] private string resultText = Strings.Get("Verify_NoResultYet");

        public bool CanCompare => !IsBusy;

        /// <summary>Пока идёт сравнение, поля вкладки недоступны.</summary>
        public bool IsNotBusy => !IsBusy;

        private CancellationTokenSource? _cts;

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanCompare));
            OnPropertyChanged(nameof(IsNotBusy));
        }

        [RelayCommand]
        private void BrowseA()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Common_AllFilesFilter") };
            if (dlg.ShowDialog() == true) FileAPath = dlg.FileName;
        }

        [RelayCommand]
        private void BrowseB()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Common_AllFilesFilter") };
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

        [RelayCommand(CanExecute = nameof(CanCompare))]
        private async Task CompareAsync()
        {
            if (!File.Exists(FileAPath) || !File.Exists(FileBPath))
            {
                await DialogService.ShowWarningAsync(Strings.Get("Common_Error"), Strings.Get("Verify_SelectBothFiles"));
                return;
            }

            _cts = new CancellationTokenSource();
            IsBusy = true;
            // Как в Конвертировании и Сборке: пока считаем хэши, переключение разделов
            // в меню заблокировано — иначе можно уйти со вкладки и потерять процесс из виду.
            OperationLockService.Instance.IsBusy = true;
            HasResult = false;
            ResultText = Strings.Get("Verify_NoResultYet");
            HashAText = NoValuePlaceholder;
            HashBText = NoValuePlaceholder;
            OverallProgress = 0;
            AppLogger.Log(Strings.Format("Verify_CompareLog", FileAPath, FileBPath));

            try
            {
                long sizeA = new FileInfo(FileAPath).Length;
                long sizeB = new FileInfo(FileBPath).Length;
                long totalWork = sizeA + sizeB;

                CurrentFileLabel = Strings.Format("Verify_FileALabelProgress", Path.GetFileName(FileAPath));
                var progressA = new Progress<(long done, long total)>(p =>
                {
                    OverallProgress = totalWork > 0 ? (double)p.done / totalWork * 100.0 : 0;
                });
                HashAText = await Task.Run(() => HashHelper.ComputeFileHashAsync(FileAPath, _cts.Token, progressA));

                CurrentFileLabel = Strings.Format("Verify_FileBLabelProgress", Path.GetFileName(FileBPath));
                var progressB = new Progress<(long done, long total)>(p =>
                {
                    OverallProgress = totalWork > 0 ? (double)(sizeA + p.done) / totalWork * 100.0 : 0;
                });
                HashBText = await Task.Run(() => HashHelper.ComputeFileHashAsync(FileBPath, _cts.Token, progressB));

                bool match = string.Equals(HashAText, HashBText, StringComparison.OrdinalIgnoreCase);
                AppLogger.Log(match ? Strings.Get("Verify_HashesMatchLog") : Strings.Get("Verify_HashesMismatchLog"));

                IsMatch = match;
                ResultText = match ? Strings.Get("Verify_MatchResult") : Strings.Get("Verify_MismatchResult");
                HasResult = true;
                StatusText = Strings.Get("Common_Done");
            }
            catch (OperationCanceledException)
            {
                StatusText = Strings.Get("Verify_Cancelled");
            }
            catch (Exception ex)
            {
                StatusText = Strings.Get("Verify_ErrorStatus");
                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), Strings.Format("Verify_HashErrorMessage", ex.Message));
            }
            finally
            {
                OverallProgress = 0;
                CurrentFileLabel = "";
                IsBusy = false;
                OperationLockService.Instance.IsBusy = false;
                _cts = null;
            }
        }

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();
    }
}
