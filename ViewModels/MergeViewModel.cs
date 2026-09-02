using System;
using System.IO;
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
    public partial class MergeViewModel : OperationTabViewModel
    {
        // Пока часть цепочки не выбрана, собирать нечего: кнопка «Начать» гаснет,
        // а не встречает нажатие сообщением об ошибке.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStart))]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private string sourcePath = "";
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

        // Положение галочек переживает перезапуск (TabOptionsService). Значения по
        // умолчанию остались прежними и заданы здесь же.
        [ObservableProperty] private bool checkDiskSpace = TabOptionsService.Get(TabOptionsService.MergeCheckDiskSpace, true);
        [ObservableProperty] private bool openFolderAfter = TabOptionsService.Get(TabOptionsService.MergeOpenFolder, true);

        partial void OnCheckDiskSpaceChanged(bool value) => TabOptionsService.Set(TabOptionsService.MergeCheckDiskSpace, value);
        partial void OnOpenFolderAfterChanged(bool value) => TabOptionsService.Set(TabOptionsService.MergeOpenFolder, value);

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;

        public bool CanStart => !IsBusy && SourcePath.Length > 0;

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
            // его подберёт та же самая логика, что и при выборе файла. Проверка
            // существования файла уходит в фон вместе с самим осмотром: этот код
            // выполняется в потоке интерфейса ровно тогда, когда открывается раздел.
            ProbeChain(delayMs: 0, resetOutputWhenSourceMissing: true);
        }

        private void ResetOutputPathToDefault() =>
            SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), DefaultOutputFileName));

        /// <summary>
        /// Тексты, заданные кодом: подпись кнопки паузы и карточка «Общая информация» —
        /// её строки собираются кодом по осмотру цепочки, а не разметкой.
        /// </summary>
        protected override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            ProbeChainNow();
        }

        protected override void OnBusyChanged(bool busy)
        {
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Подпись под полосой «Текущий файл» — там же идёт и оценка.</summary>
        protected override void ApplyCaption(string text) => CurrentFileLabel = text;

        partial void OnOverallProgressChanged(double value) => ReportTaskbarProgress(value);

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
            // То же, что в «Конвертировании»: проверка идёт в фоне (перетащенный путь может
            // вести на сетевую папку, которой сейчас нет, и ответа приходится ждать
            // секундами), а набранное за это время в поле затирать нельзя.
            string before = SourcePath;

            if (!await Task.Run(() => File.Exists(path)))
            {
                await DialogService.ShowWarningAsync(Strings.Get("Common_FileNotFoundTitle"),
                    Strings.Format("Common_FileNotFoundMessage", path));
                return;
            }

            if (!string.Equals(before, SourcePath, StringComparison.Ordinal)) return;

            SourcePath = path;
        }

        /// <summary>
        /// Осмотр цепочки — не сразу. Раньше он шёл прямо из обработчика изменения текста,
        /// то есть на каждое нажатие клавиши в поле пути, и перебирал файлы на диске
        /// в потоке интерфейса: на сетевом пути окно подвисало на каждую букву.
        /// </summary>
        private void ScheduleChainProbe() => ProbeChain(InputSettleDelayMs, resetOutputWhenSourceMissing: false);

        /// <summary>Осмотреть сейчас же, не дожидаясь, пока допечатают, — при возврате
        /// на вкладку и при смене языка. На диск всё равно ходим в фоне.</summary>
        private void ProbeChainNow() => ProbeChain(delayMs: 0, resetOutputWhenSourceMissing: false);

        /// <summary>
        /// Осмотр цепочки.
        ///
        /// <paramref name="delayMs"/> — сколько ждать, прежде чем идти на диск: при наборе
        /// текста ждём, чтобы не перебирать файлы на каждую букву, в остальных случаях нет.
        ///
        /// <paramref name="resetOutputWhenSourceMissing"/> — что делать, если файла цепочки
        /// нет: возврат на вкладку в этом случае показывает общую папку по умолчанию,
        /// потому что подбирать имя результата не по чему.
        ///
        /// На диск ходим только из Task.Run, без исключений. «Осмотреть сейчас же» раньше
        /// значило «прямо здесь», в потоке интерфейса, — и на возврате в раздел это
        /// приходилось ровно на построение страницы: осмотр делает FileInfo на каждую часть
        /// цепочки (см. MergeOperation.GetChainSize), а на флешке или сетевом пути это
        /// уже заметная задержка перехода. Именно это и написано в самом ChainProbe:
        /// он «не имеет права идти в потоке интерфейса».
        /// </summary>
        private async void ProbeChain(int delayMs, bool resetOutputWhenSourceMissing)
        {
            // Как и в «Конвертировании»: метод возвращает void, ждать его некому,
            // и необработанное исключение отсюда уронило бы программу из-за
            // предпросмотра цепочки.
            try
            {
                // Счётчик поколений отбрасывает устаревшие ответы: пока ждали или пока
                // ходили на диск, путь мог измениться ещё раз.
                int generation = ++_chainGeneration;

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                    if (generation != _chainGeneration) return;
                }

                string path = SourcePath;

                // Тип указан явно, а не выведен: без него вывод по ветке с null
                // зависит от настроек анализа, а предупреждение здесь роняет сборку.
                ChainProbeResult? probe = await Task.Run<ChainProbeResult?>(() =>
                    !resetOutputWhenSourceMissing || File.Exists(path) ? ChainProbe.Measure(path) : null);
                if (generation != _chainGeneration) return;

                if (probe is null) ResetOutputPathToDefault();
                else ApplyChainProbe(probe);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog",
                    nameof(MergeViewModel), ex.GetType().Name, ex.Message));
            }
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

            var ct = PrepareOperation();

            var progress = new Progress<MergeProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesRead / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double totalPct = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = totalPct;

                CaptionBase = Strings.Format("Common_FileProgressLabel", p.CurrentFileName, p.CurrentFileIndex, p.TotalFiles);
                UpdateCaption(p.TotalBytesProcessed, p.TotalBytes);
            });

            try
            {
                var outcome = await MergeOperation.RunAsync(
                    request, new DialogConflictResolver(), progress,
                    AppLogger.Log, Pause, ct, MarkOperationStarted);

                // При конфликте операция могла уйти в соседнее имя — показываем, куда именно.
                if (outcome.OutputPathChanged) OutputPath = outcome.OutputPath;

                // Красной полосой в панели задач отмечаем только то, что случилось само:
                // отмену человек и так помнит, а до начала работы окно ещё перед глазами —
                // там достаточно обычного сообщения.
                OperationLockService.Instance.ReportResult(
                    outcome.Status is not (MergeStatus.Failed or MergeStatus.HashMismatch));

                await ShowOutcomeAsync(outcome);
            }
            finally
            {
                FinishOperation();
            }
        }

        /// <summary>Свои полосы обнуляем здесь, всё остальное снимает базовый класс.</summary>
        protected override void FinishOperation()
        {
            OverallProgress = 0;
            CurrentFileProgress = 0;

            base.FinishOperation();
        }

        /// <summary>Вызывается операцией, когда все проверки прошли и работа началась.</summary>
        private void MarkOperationStarted()
        {
            MarkStarted();

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
            if (!string.IsNullOrEmpty(folder)) ResultFolder.Open(folder);
        }
    }
}
