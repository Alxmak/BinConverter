using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        // Два готовых программатора задают один и тот же лимит, поэтому и стоят одной
        // строкой: раздельными пунктами список обещал выбор, которого нет, — что бы
        // из двух ни выбрали, дальше всё считалось одинаково.
        public ObservableCollection<ProgrammerPreset> Presets { get; } = new()
        {
            new ProgrammerPreset("TNM5000, RT809H", FileSplitter.DefaultMaxPartSizeBytes),
            new ProgrammerPreset(Strings.Get("Convert_CustomPresetName"), null)
        };

        [ObservableProperty] private ProgrammerPreset selectedPreset = null!;

        /// <summary>
        /// Единицы рядом с полем лимита. Список постоянный, меняются только подписи
        /// при смене языка — поэтому элементы живут всё время работы программы,
        /// а не пересоздаются (на один из них указывает SelectedLimitUnit).
        /// </summary>
        public ObservableCollection<SizeUnitOption> LimitUnits { get; } = new()
        {
            new SizeUnitOption(SizeUnit.Bytes, Strings.Get("Convert_UnitBytes")),
            new SizeUnitOption(SizeUnit.Kilobytes, Strings.Get("Convert_UnitKilobytes")),
            new SizeUnitOption(SizeUnit.Megabytes, Strings.Get("Convert_UnitMegabytes")),
            new SizeUnitOption(SizeUnit.Gigabytes, Strings.Get("Convert_UnitGigabytes"))
        };

        [ObservableProperty] private SizeUnitOption selectedLimitUnit = null!;

        // Пока файл не выбран, запускать нечего: кнопка «Начать» гаснет, а не встречает
        // нажатие сообщением об ошибке.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStart))]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private string sourcePath = "";

        [ObservableProperty] private string outputFolder = "";
        [ObservableProperty] private string baseFileName = "emmc.bin";

        /// <summary>
        /// Число в поле лимита — в той единице, что выбрана рядом. Раньше здесь всегда
        /// были байты, и чтобы задать два гигабайта, приходилось набирать 2147483648,
        /// не ошибившись ни в одной из десяти цифр. Ошибка на разряд означает здесь
        /// вдесятеро больше файлов или один вместо десяти, и замечается она уже
        /// по готовой нарезке.
        /// </summary>
        [ObservableProperty] private string limitValueText = "";

        /// <summary>Точное число байт под полем — то, что раньше стояло в самом поле.</summary>
        [ObservableProperty] private string limitBytesHint = "";

        [ObservableProperty] private string generalInfoText = "";
        [ObservableProperty] private string displayedFilesText = "";

        /// <summary>
        /// Есть ли что показать в «Общей информации». Пока файла нет, карточка показывает
        /// подсказку вместо строк: раньше на месте значений стояли прочерки, и карточка
        /// выглядела заполненной — как будто размер и количество уже посчитаны.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowInfoHint))]
        private bool hasInfo;

        public bool ShowInfoHint => !HasInfo;

        [ObservableProperty] private bool showExpandButton;
        [ObservableProperty] private bool showAllFiles;
        [ObservableProperty] private string expandButtonText = Strings.Get("Convert_ShowAllFilesButton");

        // Положение галочек переживает перезапуск (TabOptionsService). Значения по
        // умолчанию остались прежними и заданы здесь же: служба знает только то, что
        // человек менял, а нетронутая галочка в файл не попадает вовсе.
        [ObservableProperty] private bool verifyHashAfter = TabOptionsService.Get(TabOptionsService.ConvertVerifyHash, true);
        [ObservableProperty] private bool openFolderAfter = TabOptionsService.Get(TabOptionsService.ConvertOpenFolder, true);
        [ObservableProperty] private bool checkDiskSpace = TabOptionsService.Get(TabOptionsService.ConvertCheckDiskSpace, true);

        partial void OnVerifyHashAfterChanged(bool value) => TabOptionsService.Set(TabOptionsService.ConvertVerifyHash, value);
        partial void OnOpenFolderAfterChanged(bool value) => TabOptionsService.Set(TabOptionsService.ConvertOpenFolder, value);
        partial void OnCheckDiskSpaceChanged(bool value) => TabOptionsService.Set(TabOptionsService.ConvertCheckDiskSpace, value);

        // Доступность всех трёх кнопок операции решается через CanExecute: общая строка
        // кнопок (OperationBar) ничего не знает про IsBusy и не привязывает IsEnabled сама.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        private bool isBusy;

        [ObservableProperty] private bool isPaused;
        [ObservableProperty] private string pauseButtonText = Strings.Get("Common_PauseButton");

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        private bool isVerifying;

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;
        [ObservableProperty] private double shaProgress;

        public bool CanStart => !IsBusy && SourcePath.Length > 0;
        public bool CanPause => IsBusy && !IsVerifying;

        /// <summary>Пока идёт операция, поля и параметры вкладки недоступны — менять их
        /// на ходу нельзя, иначе настройки разойдутся с уже запущенным процессом.</summary>
        public bool IsNotBusy => !IsBusy;
        public bool IsCustomPreset => SelectedPreset?.IsCustom == true;

        private CancellationTokenSource? _cts;
        private PauseController? _pauseController;

        /// <summary>
        /// Оценка «сколько осталось» под полосой прогресса. Часы отдельные, а не
        /// DateTime.Now в каждом отсчёте: разность двух моментов Stopwatch не зависит
        /// от того, перевели ли за это время системные часы.
        /// </summary>
        private readonly SpeedEstimator _speed = new();
        private readonly Stopwatch _clock = new();

        /// <summary>
        /// Начало подписи под полосой — то, что показывалось и до появления оценки.
        /// Хранится отдельно, потому что при проверке хэша отсчёты идут дальше, а имя
        /// текущего файла меняться уже не будет: подпись нужно пересобрать, не потеряв
        /// её первую половину.
        /// </summary>
        private string _captionBase = "";

        /// <summary>Сколько ждать после последнего нажатия, прежде чем идти на диск.</summary>
        private const int InputSettleDelayMs = 250;

        private int _previewGeneration;
        private int _expectedFileCount;
        private long _expectedTotalSize;
        private long _expectedLimitBytes;

        // Папку назначения человек мог задать сам, а мог оставить как есть. Пока она
        // «как есть», подставляем актуальный путь по умолчанию из «Настроек» — раньше
        // он перечитывался сам, потому что ViewModel создавалась заново на каждый переход.
        // Тот же приём и по той же причине уже есть в Сборке.
        private bool _outputFolderIsAuto = true;
        private bool _settingOutputFolderInternally;

        public ConvertViewModel()
        {
            // Единица до пресета: выбор пресета подставляет и число, и единицу, а без
            // выбранной строки в списке она подставилась бы в пустоту.
            SelectedLimitUnit = LimitUnits[0];
            SelectedPreset = Presets[0];
            SetOutputFolderAuto(GetDefaultOutputFolder());
        }

        protected override void OnAttached()
        {
            if (_outputFolderIsAuto) SetOutputFolderAuto(GetDefaultOutputFolder());
        }

        private void SetOutputFolderAuto(string path)
        {
            _settingOutputFolderInternally = true;
            OutputFolder = path;
            _settingOutputFolderInternally = false;
        }

        /// <summary>
        /// Тексты, заданные кодом, после смены языка: имя пользовательского пресета
        /// в списке, подпись кнопки списка файлов, подпись кнопки паузы и вся
        /// «Общая информация» — она собирается в UpdatePreview, а не разметкой.
        /// </summary>
        protected override void OnLanguageChanged()
        {
            Presets[^1].Name = Strings.Get("Convert_CustomPresetName");
            LimitUnits[0].Name = Strings.Get("Convert_UnitBytes");
            LimitUnits[1].Name = Strings.Get("Convert_UnitKilobytes");
            LimitUnits[2].Name = Strings.Get("Convert_UnitMegabytes");
            LimitUnits[3].Name = Strings.Get("Convert_UnitGigabytes");
            PauseButtonText = Strings.Get(IsPaused ? "Common_ResumeButton" : "Common_PauseButton");
            UpdateLimitHint();
            UpdatePreviewNow();
        }

        private static string GetDefaultOutputFolder() => OutputPathSettingsService.GetConvertFolder();

        // Разбор лимита — в PartSizeLimit из Core: от этого числа зависит, на сколько частей
        // разрежется прошивка, поэтому граничные случаи проверяются тестами.
        private long CurrentLimitBytes =>
            PartSizeLimit.Resolve(SelectedPreset?.MaxPartSizeBytes, LimitValueText, SelectedLimitUnit?.Unit ?? SizeUnit.Bytes);

        partial void OnSelectedPresetChanged(ProgrammerPreset value)
        {
            OnPropertyChanged(nameof(IsCustomPreset));
            if (value?.MaxPartSizeBytes is long fixedBytes) ShowLimitBytes(fixedBytes);
            UpdateLimitHint();
            UpdatePreviewNow();
        }

        /// <summary>
        /// Показывает готовый лимит в поле: единицу берём самую крупную, в которой он
        /// выражается целым числом. Так у пресета вместо 4 152 360 960 в поле стоит
        /// «3960 МБ» — то же самое, но это число можно запомнить и пересказать.
        /// </summary>
        private void ShowLimitBytes(long bytes)
        {
            var unit = SizeUnits.Best(bytes);

            SelectedLimitUnit = LimitUnits.FirstOrDefault(option => option.Unit == unit) ?? LimitUnits[0];
            LimitValueText = SizeUnits.ToUnit(bytes, unit).ToString(CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// Точное число байт под полем. Единица делает лимит читаемым, но программатору
        /// человек задаёт именно байты, и сверить их с тем, что написано у него в окне,
        /// должно быть можно не считая в уме.
        /// </summary>
        private void UpdateLimitHint()
        {
            long bytes = CurrentLimitBytes;

            // В байтах подпись повторяла бы само поле слово в слово.
            LimitBytesHint = bytes > 0 && SelectedLimitUnit?.Unit != SizeUnit.Bytes
                ? Strings.Format("Convert_LimitBytesHint", bytes)
                : "";
        }

        partial void OnLimitValueTextChanged(string value)
        {
            UpdateLimitHint();
            SchedulePreviewUpdate();
        }

        /// <summary>
        /// Смена единицы не пересчитывает число: «500» при переключении с МБ на ГБ
        /// становится 500 гигабайтами, а не 0.48. Так работает любой такой список,
        /// и человек, меняющий единицу, меняет её как раз затем, чтобы то же число
        /// значило другое.
        /// </summary>
        partial void OnSelectedLimitUnitChanged(SizeUnitOption value)
        {
            UpdateLimitHint();
            UpdatePreviewNow();
        }
        // Пункт: поле пути теперь редактируется свободно (можно вставлять и печатать) —
        // предпросмотр должен обновляться и при прямом вводе, не только через SetSource.
        partial void OnSourcePathChanged(string value) => SchedulePreviewUpdate();
        partial void OnShowAllFilesChanged(bool value) => RebuildFilesText();

        partial void OnOutputFolderChanged(string value)
        {
            if (!_settingOutputFolderInternally) _outputFolderIsAuto = false;
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(IsNotBusy));
        }
        partial void OnIsVerifyingChanged(bool value) => OnPropertyChanged(nameof(CanPause));

        // Полоса на кнопке в панели задач показывает тот же общий ход работы, что и полоса
        // на странице, — чтобы за ним не приходилось разворачивать свёрнутое окно.
        partial void OnOverallProgressChanged(double value) =>
            OperationLockService.Instance.Progress = value / 100.0;

        partial void OnIsPausedChanged(bool value)
        {
            OperationLockService.Instance.IsPaused = value;

            // На паузе оценка врёт: время идёт, а байты — нет. Убираем её из подписи,
            // а после возобновления она посчитается заново, с чистого листа: иначе первые
            // секунды после паузы показывали бы скорость, размазанную по простою.
            if (!value) return;

            _speed.Reset();
            CurrentFileLabel = _captionBase;
        }

        [RelayCommand]
        private async Task BrowseSourceAsync()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Common_BinFileFilter") };
            if (dlg.ShowDialog() == true) await SetSourceAsync(dlg.FileName);
        }

        /// <summary>Асинхронный, потому что о ненайденном файле сообщает окно в стиле
        /// программы (<see cref="DialogService"/>), а не системный MessageBox.</summary>
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
            UpdatePreviewNow();
        }

        [RelayCommand]
        private void ToggleExpand() => ShowAllFiles = !ShowAllFiles;

        // ============================= Предпросмотр =============================

        // До выбора файла строк нет вовсе: карточка показывает подсказку (HasInfo=false).
        // Прочерки напротив подписей, которые стояли здесь раньше, читались как готовый
        // ответ «ноль», а не как «пока нечего показать».

        /// <summary>
        /// Пересчёт предпросмотра после набора текста — не сразу. Раньше он вызывался прямо
        /// из обработчика изменения текста, то есть на каждое нажатие клавиши в поле пути,
        /// и каждый раз спрашивал файловую систему в потоке интерфейса: на локальном диске
        /// незаметно, на сетевом пути окно подвисало на каждую букву.
        /// </summary>
        private void SchedulePreviewUpdate() => MeasureAndUpdatePreview(InputSettleDelayMs);

        /// <summary>Пересчитать сейчас же, не дожидаясь, пока допечатают, — выбор пресета,
        /// «Обзор», перетаскивание, смена языка. На диск всё равно идём в фоне.</summary>
        private void UpdatePreviewNow() => MeasureAndUpdatePreview(delayMs: 0);

        /// <summary>
        /// Общий путь пересчёта. Счётчик поколений отбрасывает устаревшие вызовы: пока
        /// ждали или пока ходили на диск, путь мог измениться ещё раз, и старый ответ
        /// затёр бы новый.
        ///
        /// Размер файла спрашивается только из Task.Run. «Пересчитать сейчас же» раньше
        /// значило «прямо здесь», в потоке интерфейса, и вызывается это в том числе
        /// из OnLanguageChanged — то есть при открытии раздела, когда страница и так
        /// строится. Один FileInfo на локальном диске незаметен, на сетевом пути и
        /// на отключённой флешке — нет: там он ждёт таймаута файловой системы.
        /// Ровно та же правка уже сделана в «Сборке файла».
        /// </summary>
        private async void MeasureAndUpdatePreview(int delayMs)
        {
            // Метод возвращает void: его вызывает обработчик изменения свойства, ждать
            // его некому. Значит, любое исключение отсюда становится необработанным
            // и роняет программу целиком — из-за предпросмотра, без которого можно жить.
            // Поэтому тело закрыто целиком, а причина уходит в журнал.
            try
            {
                int generation = ++_previewGeneration;

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                    if (generation != _previewGeneration) return;
                }

                string path = SourcePath;
                var probe = await Task.Run(() => FileProbe.Measure(path));
                if (generation != _previewGeneration) return;

                UpdatePreview(probe);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog",
                    nameof(ConvertViewModel), ex.GetType().Name, ex.Message));
            }
        }

        private void UpdatePreview(FileProbeResult source)
        {
            long limit = CurrentLimitBytes;

            if (!source.Exists)
            {
                HasInfo = false;
                GeneralInfoText = "";
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = "";
                return;
            }

            HasInfo = true;
            long size = source.SizeBytes;

            if (limit < 1024)
            {
                // Количество файлов не считается, пока лимит не задан, — вместо числа
                // отдельная строка о том, что поправить. Раньше на его месте стоял
                // прочерк с пояснением в скобках следом.
                GeneralInfoText =
                    Strings.Format("Common_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                    Strings.Get("Convert_InvalidLimitLine");
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = "";
                return;
            }

            int count;
            try
            {
                count = FileSplitter.CalculateExpectedPartCount(size, limit);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Частей вышло бы больше, чем программа может создать (крошечный лимит
                // на огромном файле). Для предпросмотра это тот же случай, что и неверный
                // лимит: показываем, что поправить, вместо числа. Ловим здесь, а не
                // считаем «как-нибудь»: предпросмотр идёт из async void, и исключение
                // отсюда стало бы необработанным и уронило бы программу.
                GeneralInfoText =
                    Strings.Format("Common_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                    Strings.Get("Convert_InvalidLimitLine");
                _expectedFileCount = 0;
                ShowExpandButton = false;
                DisplayedFilesText = "";
                return;
            }

            if (count > MaxFilesToEnumerate)
            {
                GeneralInfoText =
                    Strings.Format("Common_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                    Strings.Format("Convert_ExpectedCountLine", count.ToString("N0"));
                _expectedFileCount = count;
                ShowExpandButton = false;
                DisplayedFilesText = Strings.Get("Convert_TooManyFilesLine");
                return;
            }

            _expectedFileCount = count;
            _expectedTotalSize = size;
            _expectedLimitBytes = limit;

            GeneralInfoText =
                Strings.Format("Common_SourceSizeLine", SizeFormatHelper.Format(size)) + "\n" +
                Strings.Format("Convert_ExpectedCountLine", count);

            ShowExpandButton = count > CollapsedFileListCount;
            ShowAllFiles = false;
            RebuildFilesText();
        }

        private void RebuildFilesText()
        {
            if (_expectedFileCount == 0) { DisplayedFilesText = ""; return; }

            int toShow = ShowAllFiles ? _expectedFileCount : Math.Min(CollapsedFileListCount, _expectedFileCount);

            var sb = new StringBuilder();
            sb.AppendLine(Strings.Get("Convert_EachFileSizeHeader"));

            // Считаем только показываемые части. Раньше цикл шёл по всем — до двух тысяч
            // проходов, из которых пригождались четыре, — потому что размер очередной
            // части набирался вычитанием. Размер части считается сразу по её номеру,
            // и той же формулой, что у самой нарезки (FileSplitter.PartSizeAt).
            for (int i = 1; i <= toShow; i++)
            {
                long partSize = FileSplitter.PartSizeAt(_expectedTotalSize, _expectedLimitBytes, i - 1);
                sb.AppendLine(Strings.Format("Convert_FileSizeLine", i, SizeFormatHelper.Format(partSize)));
            }

            int hidden = _expectedFileCount - CollapsedFileListCount;
            if (!ShowAllFiles && hidden > 0)
                sb.AppendLine(Strings.Format("Convert_MoreFilesLine", hidden, PluralForms.Files(hidden)));

            ExpandButtonText = ShowAllFiles
                ? Strings.Get("Convert_CollapseList")
                : Strings.Format("Convert_ShowAllFilesCount", _expectedFileCount, PluralForms.Files(_expectedFileCount));

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
            long sourceSize = FileProbe.Measure(SourcePath).SizeBytes;
            long totalWorkBytes = sourceSize * (VerifyHashAfter ? 2 : 1);

            var splitProgress = new Progress<SplitProgress>(p =>
            {
                double filePct = p.CurrentFileSizeBytes > 0 ? (double)p.CurrentFileBytesWritten / p.CurrentFileSizeBytes * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)p.TotalBytesWritten / totalWorkBytes * 100.0 : 100.0;
                CurrentFileProgress = filePct;
                OverallProgress = overallPct;

                _captionBase = Strings.Format("Common_FileProgressLabel", p.CurrentFileName, p.CurrentFileIndex, p.TotalFiles);
                UpdateCaption(p.TotalBytesWritten, totalWorkBytes);
            });

            var hashProgress = new Progress<(long done, long total)>(p =>
            {
                if (!IsVerifying)
                {
                    IsVerifying = true;
                    // Фаза проверки хэша паузу не поддерживает, и оставленная пауза выглядела бы
                    // как зависший бар — снимаем её сами.
                    if (IsPaused) { _pauseController?.Resume(); IsPaused = false; PauseButtonText = Strings.Get("Common_PauseButton"); }
                }
                ShaProgress = p.total > 0 ? (double)p.done / p.total * 100.0 : 100.0;
                double overallPct = totalWorkBytes > 0 ? (double)(sourceSize + p.done) / totalWorkBytes * 100.0 : 100.0;
                OverallProgress = Math.Min(100.0, overallPct);

                // Проверка хэша — вторая половина той же работы, и оценка идёт по ней
                // сквозной: иначе на середине операции «осталось» скакнуло бы вдвое.
                UpdateCaption(sourceSize + p.done, totalWorkBytes);
            });

            try
            {
                var outcome = await ConvertOperation.RunAsync(
                    request, new DialogConflictResolver(), splitProgress, hashProgress,
                    AppLogger.Log, _pauseController, _cts.Token, MarkOperationStarted);

                // При конфликте операция могла уйти в соседнюю папку — показываем, куда именно.
                if (outcome.OutputFolderChanged) OutputFolder = outcome.OutputFolder;

                // Красной полосой в панели задач отмечаем только то, что случилось само:
                // отмену человек и так помнит, а до начала работы окно ещё перед глазами —
                // там достаточно обычного сообщения.
                OperationLockService.Instance.ReportResult(
                    outcome.Status is not (ConvertStatus.Failed or ConvertStatus.HashMismatch));

                await ShowOutcomeAsync(outcome);
            }
            finally
            {
                OverallProgress = 0; CurrentFileProgress = 0; ShaProgress = 0;
                IsBusy = false;
                IsPaused = false;
                IsVerifying = false;
                OperationLockService.Instance.OperationFinished();
                // Dispose до обнуления: и здесь, и в Cancel мы в потоке интерфейса,
                // поэтому «отменить уже освобождённый» невозможно.
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
            IsVerifying = false;
            OperationLockService.Instance.OperationStarted(CancelNow);
            PauseButtonText = Strings.Get("Common_PauseButton");
            OverallProgress = 0; CurrentFileProgress = 0; ShaProgress = 0;

            // Часы пускаются здесь, а не при нажатии «Начать»: между нажатием и первым
            // байтом успевают пройти проверки места и вопрос о перезаписи, а ждать ответа
            // человека — не работа, и в скорость это попадать не должно.
            _speed.Reset();
            _clock.Restart();
        }

        /// <summary>
        /// Пересобирает подпись под полосой: к тому, что показывалось раньше, добавляются
        /// оценка оставшегося времени и скорость — если их уже есть из чего посчитать.
        /// </summary>
        private void UpdateCaption(long doneBytes, long totalBytes)
        {
            _speed.Add(_clock.Elapsed, doneBytes);
            CurrentFileLabel = ProgressCaption.Build(_captionBase, _speed.BytesPerSecond, _speed.Remaining(totalBytes));
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
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"), Strings.Get("Convert_SelectSourceFirst"));
                    break;

                case ConvertStatus.OutputFolderNotSpecified:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"), Strings.Get("Convert_SpecifyOutputFolder"));
                    break;

                case ConvertStatus.InvalidPartSize:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"), Strings.Get("Convert_InvalidPartSize"));
                    break;

                // Обе ситуации — «нельзя начать»: ни одного файла ещё не создано,
                // и дело в настройках, а не в сорвавшейся работе.
                case ConvertStatus.SourceInsideOutput:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Get("Convert_SourceInsideOutputMessage"));
                    break;

                case ConvertStatus.OutputNotUsable:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Format("Common_OutputNotUsableMessage", outcome.ErrorMessage));
                    break;

                case ConvertStatus.CancelledBeforeStart:
                    // Пользователь сам отказался в диалоге конфликта — сообщать ему об этом нечего.
                    break;

                case ConvertStatus.NotEnoughSpace:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_LowSpaceTitle"),
                        Strings.Format("Convert_LowSpaceMessage",
                            SizeFormatHelper.Format(outcome.SourceSizeBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.RequiredBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.AvailableBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.MissingBytes)));
                    break;

                case ConvertStatus.CompletedVerified:
                    // Хэш отдаётся отдельным полем, а не внутри текста: рядом с ним
                    // в окне стоит кнопка копирования — его вписывают в «Ожидаемый
                    // SHA-256» при обратной сборке.
                    await DialogService.ShowInfoWithHashesAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Format("Convert_DoneVerifiedMessage", outcome.PartsCreated),
                        new DialogService.HashRow(Strings.Get("Common_HashConfirmedLabel"), outcome.SourceHash));
                    OpenResultFolder(outcome.OutputFolder);
                    break;

                case ConvertStatus.HashMismatch:
                    // Оба хэша — с кнопками копирования: именно с ними идут разбираться,
                    // почему части не сложились в исходный файл.
                    await DialogService.ShowErrorWithHashesAsync(Strings.Get("Common_VerifyErrorTitle"),
                        Strings.Get("Convert_VerifyErrorMessage"),
                        new DialogService.HashRow(Strings.Get("Convert_HashSourceLabel"), outcome.SourceHash),
                        new DialogService.HashRow(Strings.Get("Convert_HashResultLabel"), outcome.RecombinedHash));
                    OpenResultFolder(outcome.OutputFolder);
                    break;

                case ConvertStatus.Completed:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Format("Convert_DoneMessage", outcome.PartsCreated));
                    OpenResultFolder(outcome.OutputFolder);
                    break;

                case ConvertStatus.Cancelled:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_CancelledTitle"),
                        Strings.Format("Convert_CancelledMessage", outcome.CreatedFileCount));
                    break;

                case ConvertStatus.Failed:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), outcome.DiskFull
                        ? Strings.Format("Convert_DiskFullMessage", outcome.CreatedFileCount)
                        : Strings.Format("Convert_ErrorMessage", outcome.ErrorMessage, outcome.CreatedFileCount));
                    break;
            }
        }

        private void OpenResultFolder(string folder)
        {
            if (OpenFolderAfter) ResultFolder.Open(folder);
        }

        /// <summary>
        /// Спрашивает, прежде чем прервать. Раньше не спрашивала ни кнопка, ни Esc,
        /// а цена ошибки здесь — вся работа: недописанные файлы удаляются, и на дампе
        /// в несколько гигабайт это десятки минут заново. Esc вдобавок рефлекторная
        /// клавиша «закрыть что-нибудь ненужное», а «Отмена» — крупная красная кнопка
        /// в восьми пикселях под «Начать».
        /// </summary>
        [RelayCommand(CanExecute = nameof(IsBusy))]
        private async Task CancelAsync()
        {
            var answer = await DialogService.ShowConfirmAsync(
                Strings.Get("Common_CancelConfirmTitle"),
                Strings.Get("Common_CancelConfirmWritingMessage"),
                Strings.Get("Common_CancelConfirmStop"),
                null,
                Strings.Get("Common_CancelConfirmKeep"));

            if (answer == DialogChoice.Primary) CancelNow();
        }

        /// <summary>
        /// Прервать молча. Тем же способом операцию останавливает закрытие окна — там
        /// решение уже принято в своём диалоге, и спрашивать второй раз незачем.
        /// </summary>
        private void CancelNow()
        {
            _pauseController?.Resume();
            _cts?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(CanPause))]
        private void TogglePause()
        {
            if (_pauseController == null || IsVerifying) return;

            if (IsPaused)
            {
                _pauseController.Resume();
                IsPaused = false;
                PauseButtonText = Strings.Get("Common_PauseButton");
                AppLogger.Log(Strings.Get("Common_ResumedLog"));
            }
            else
            {
                _pauseController.Pause();
                IsPaused = true;
                PauseButtonText = Strings.Get("Common_ResumeButton");
                AppLogger.Log(Strings.Get("Common_PausedLog"));
            }
        }

    }
}
