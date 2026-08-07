using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Operations;
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
            var request = new VerifyRequest { FileAPath = FileAPath, FileBPath = FileBPath };

            _cts = new CancellationTokenSource();

            var progress = new Progress<VerifyProgress>(p =>
            {
                OverallProgress = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 0;
                CurrentFileLabel = Strings.Format(
                    p.FileIndex == 1 ? "Verify_FileALabelProgress" : "Verify_FileBLabelProgress", p.FileName);
            });

            try
            {
                var outcome = await VerifyOperation.RunAsync(
                    request, progress, AppLogger.Log, _cts.Token, MarkOperationStarted);

                // Хэши показываем и при отмене: посчитанное на экране пропадать не должно.
                if (outcome.HashA.Length > 0) HashAText = outcome.HashA;
                if (outcome.HashB.Length > 0) HashBText = outcome.HashB;

                await ShowOutcomeAsync(outcome);
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

        /// <summary>Вызывается операцией, когда файлы найдены и сравнение началось.</summary>
        private void MarkOperationStarted()
        {
            IsBusy = true;
            // Как в Конвертировании и Сборке: пока считаем хэши, переключение разделов
            // в меню заблокировано — иначе можно уйти со вкладки и потерять процесс из виду.
            OperationLockService.Instance.IsBusy = true;
            HasResult = false;
            ResultText = Strings.Get("Verify_NoResultYet");
            HashAText = NoValuePlaceholder;
            HashBText = NoValuePlaceholder;
            OverallProgress = 0;
        }

        private async Task ShowOutcomeAsync(VerifyOutcome outcome)
        {
            switch (outcome.Status)
            {
                case VerifyStatus.FileMissing:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_Error"), Strings.Get("Verify_SelectBothFiles"));
                    break;

                case VerifyStatus.Match:
                case VerifyStatus.Mismatch:
                    IsMatch = outcome.Status == VerifyStatus.Match;
                    ResultText = Strings.Get(IsMatch ? "Verify_MatchResult" : "Verify_MismatchResult");
                    HasResult = true;
                    break;

                case VerifyStatus.Cancelled:
                    // Отмену человек запросил сам — окно с сообщением об этом только мешало бы.
                    break;

                case VerifyStatus.Failed:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"),
                        Strings.Format("Verify_HashErrorMessage", outcome.ErrorMessage));
                    break;
            }
        }

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();
    }
}
