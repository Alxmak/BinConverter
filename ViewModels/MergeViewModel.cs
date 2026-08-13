using System;
using System.Diagnostics;
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
    public partial class MergeViewModel : LogHostViewModel
    {
        [ObservableProperty] private string sourcePath = "";
        // Пусто до выбора файла: подсказку про перетаскивание убрали из карточки
        // "Общая информация" — там место для сведений о цепочке, а не для инструкции.
        [ObservableProperty] private string chainInfoText = "";

        // Карточка "Общая информация" из макета: размер файла, который получится после
        // склейки. Считается по уже найденной цепочке, отдельного прохода по диску не нужно.
        // Пусто до выбора части: карточка показывает подсказку, а не строку с прочерком
        // вместо размера — прочерк читался как посчитанный ноль.
        [ObservableProperty] private string resultSizeText = "";

        /// <summary>
        /// Есть ли что показать в «Общей информации»: найденная цепочка или сообщение о том,
        /// почему её собрать не вышло. Пока путь пуст, вместо строк идёт подсказка.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowInfoHint))]
        private bool hasInfo;

        public bool ShowInfoHint => !HasInfo;

        [ObservableProperty] private string outputPath = "";
        [ObservableProperty] private string expectedHashText = "";

        [ObservableProperty] private bool checkDiskSpace = true;
        [ObservableProperty] private bool openFolderAfter = true;

        // Доступность всех трёх кнопок операции — через CanExecute, см. то же в Конвертировании.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
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

        /// <summary>Сколько ждать после последнего нажатия, прежде чем идти на диск.</summary>
        private const int InputSettleDelayMs = 250;

        private int _chainGeneration;

        // Пункт: "Папка назначения" должна показывать путь по умолчанию сразу при запуске
        // (как в Конвертировании), а не только после выбора файла цепочки. Пока путь не
        // менялся пользователем явно (набор текста/вставка/"Обзор..."), автоматически
        // подставляем актуальный путь по умолчанию — сначала общий, потом с именем исходника.
        private bool _outputPathIsAuto = true;
        private bool _settingOutputPathInternally;

        private const string DefaultOutputFileName = "emmc_merged.bin";

        public MergeViewModel()
        {
            ResetOutputPathToDefault();
        }

        /// <summary>
        /// Папку по умолчанию могли поменять в «Настройках», пока мы были не на экране.
        /// Раньше это подхватывалось само, потому что ViewModel создавалась заново
        /// на каждый переход между разделами.
        /// </summary>
        protected override void OnAttached()
        {
            if (!_outputPathIsAuto) return;

            // Если файл цепочки уже выбран, имя результата зависит от него — пусть
            // его подберёт та же самая логика, что и при выборе файла.
            if (File.Exists(SourcePath)) ProbeChainNow();
            else ResetOutputPathToDefault();
        }

        private void ResetOutputPathToDefault() =>
            SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), DefaultOutputFileName));

        /// <summary>
        /// Тексты, заданные кодом: подпись кнопки паузы и карточка «Общая информация» —
        /// её строки собираются кодом по осмотру цепочки, а не разметкой.
        /// </summary>
        protected override void OnLanguageChanged()
        {
            PauseButtonText = Strings.Get(IsPaused ? "Common_ResumeButton" : "Common_PauseButton");
            ProbeChainNow();
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(IsNotBusy));
        }

        // Пункт: поле пути теперь редактируется свободно (можно вставлять и печатать) —
        // цепочка частей должна осматриваться и при прямом вводе, не только через SetSource.
        partial void OnSourcePathChanged(string value) => ScheduleChainProbe();

        partial void OnOutputPathChanged(string value)
        {
            if (!_settingOutputPathInternally) _outputPathIsAuto = false;
        }

        /// <summary>
        /// Меняет только папку результата, имя файла оставляет прежним. Нужно для
        /// перетаскивания папки на строку назначения: путь здесь — файл, и подставлять
        /// вместо него папку было бы неверно.
        /// </summary>
        public void SetOutputFolderKeepingFileName(string folder)
        {
            string name = Path.GetFileName(OutputPath);
            if (string.IsNullOrEmpty(name)) name = DefaultOutputFileName;

            OutputPath = Path.Combine(folder, name);
        }

        private void SetOutputPathAuto(string path)
        {
            _settingOutputPathInternally = true;
            OutputPath = path;
            _settingOutputPathInternally = false;
        }

        [RelayCommand]
        private async Task BrowseSourceAsync()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Merge_SourceFileFilter") };
            if (dlg.ShowDialog() == true) await SetSourceAsync(dlg.FileName);
        }

        /// <summary>Асинхронный по той же причине, что и в Конвертировании: сообщение
        /// о ненайденном файле идёт через <see cref="DialogService"/>.</summary>
        public async Task SetSourceAsync(string path)
        {
            if (!File.Exists(path))
            {
                await DialogService.ShowWarningAsync(Strings.Get("Common_FileNotFoundTitle"),
                    Strings.Format("Common_FileNotFoundMessage", path));
                return;
            }

            SourcePath = path;
        }

        /// <summary>
        /// Осмотр цепочки — не сразу. Раньше он шёл прямо из обработчика изменения текста,
        /// то есть на каждое нажатие клавиши в поле пути, и перебирал файлы на диске
        /// в потоке интерфейса: на сетевом пути окно подвисало на каждую букву.
        ///
        /// Счётчик поколений отбрасывает устаревшие ответы: пока ждали или пока ходили
        /// на диск, путь мог измениться ещё раз.
        /// </summary>
        private async void ScheduleChainProbe()
        {
            // Как и в «Конвертировании»: метод возвращает void, ждать его некому,
            // и необработанное исключение отсюда уронило бы программу из-за
            // предпросмотра цепочки.
            try
            {
                int generation = ++_chainGeneration;

                await Task.Delay(InputSettleDelayMs);
                if (generation != _chainGeneration) return;

                string path = SourcePath;
                var probe = await Task.Run(() => ChainProbe.Measure(path));
                if (generation != _chainGeneration) return;

                ApplyChainProbe(probe);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog",
                    nameof(MergeViewModel), ex.GetType().Name, ex.Message));
            }
        }

        /// <summary>Осмотреть сейчас же — когда путь задан не набором текста
        /// (кнопка «Обзор», перетаскивание, возврат на вкладку, смена языка).</summary>
        private void ProbeChainNow()
        {
            _chainGeneration++;
            ApplyChainProbe(ChainProbe.Measure(SourcePath));
        }

        private void ApplyChainProbe(ChainProbeResult probe)
        {
            if (!probe.Resolved)
            {
                // Пустой путь — это ещё не ошибка, просто нечего показывать: тогда карточка
                // показывает подсказку. А вот причину, по которой цепочку собрать не вышло,
                // прятать за подсказкой нельзя — это ответ на выбранный файл.
                ChainInfoText = probe.ErrorMessage.Length == 0
                    ? ""
                    : Strings.Format("Merge_ChainResolveError", probe.ErrorMessage);
                HasInfo = ChainInfoText.Length > 0;
                ResultSizeText = "";
                return;
            }

            HasInfo = true;
            ChainInfoText = Strings.Format("Merge_ChainFoundInfo",
                probe.PartCount, SizeFormatHelper.Format(probe.TotalBytes), probe.BaseFileName);

            // Неровные размеры частей — предупреждение, а не отказ: собрать такую цепочку
            // можно, но человеку надо знать об этом до запуска, а не после.
            if (probe.UnevenPartNumber is int uneven)
                ChainInfoText += "\n" + Strings.Format("Merge_UnevenPartsWarning", uneven);
            ResultSizeText = Strings.Format("Merge_ResultSizeLine", SizeFormatHelper.Format(probe.TotalBytes));

            if (_outputPathIsAuto)
                SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), probe.SuggestedOutputFileName));
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new SaveFileDialog { Filter = Strings.Get("Common_BinFileFilter"), FileName = DefaultOutputFileName };
            if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            var request = new MergeRequest
            {
                AnyChainFilePath = SourcePath,
                OutputPath = OutputPath,
                CheckDiskSpace = CheckDiskSpace,
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
                _cts?.Dispose();
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

                // Самая дорогая из ошибок, которые программа может допустить: сборка
                // поверх собираемой цепочки уничтожила бы её часть. Поэтому не «Ошибка»
                // после, а «Нельзя начать» до.
                case MergeStatus.OutputInsideChain:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Get("Merge_OutputInsideChainMessage"));
                    break;

                case MergeStatus.OutputNotUsable:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Format("Common_OutputNotUsableMessage", outcome.ErrorMessage));
                    break;

                case MergeStatus.CancelledBeforeStart:
                    // Пользователь сам отказался перезаписывать файл — сообщать ему об этом нечего.
                    break;

                // Ловим до начала работы: раньше опечатка в поле доезжала до конца сборки
                // и выдавала «Ошибка проверки» — как будто испорчен файл, а не введённая строка.
                case MergeStatus.ExpectedHashInvalid:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Format("Merge_ExpectedHashInvalidMessage", Sha256Text.HexLength));
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
                    await DialogService.ShowInfoWithHashesAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Get("Merge_ResultMatchMessage"),
                        new DialogService.HashRow(Strings.Get("Common_HashConfirmedLabel"), outcome.MergedHash));
                    OpenResultFolder(outcome.OutputPath);
                    break;

                case MergeStatus.HashMismatch:
                    await DialogService.ShowErrorWithHashesAsync(Strings.Get("Common_VerifyErrorTitle"),
                        Strings.Get("Merge_ResultMismatchMessage"),
                        new DialogService.HashRow(Strings.Get("Merge_HashExpectedLabel"), Sha256Text.Normalize(ExpectedHashText)),
                        new DialogService.HashRow(Strings.Get("Merge_HashActualLabel"), outcome.MergedHash));
                    OpenResultFolder(outcome.OutputPath);
                    break;

                case MergeStatus.Completed:
                    // Ожидаемый хэш не задавали, сверять было не с чем — поэтому подпись
                    // не «подтверждён», а просто «хэш собранного файла».
                    await DialogService.ShowInfoWithHashesAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Format("Merge_DoneMessage", outcome.PartsUsed, outcome.TotalBytes),
                        new DialogService.HashRow(Strings.Get("Merge_HashResultLabel"), outcome.MergedHash));
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
            catch (Exception ex) { AppLogger.Log(Strings.Format("Common_OpenFolderFailedLog", ex.Message)); }
        }
        [RelayCommand(CanExecute = nameof(IsBusy))]
        private void Cancel()
        {
            _pauseController?.Resume();
            _cts?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(CanPause))]
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
