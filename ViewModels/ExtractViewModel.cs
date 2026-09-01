using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Operations;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Services;

// Только ради Dispatcher'а в AskNandGeometryAsync. Пространство System.Windows целиком
// не подключаем: в нём свои MessageBox и Point, и они пересекаются с уже используемыми
// именами — про эти грабли сказано в CLAUDE.md.
using Application = System.Windows.Application;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// Строка таблицы разделов. Отдельный тип, а не <see cref="PartitionEntry"/> напрямую:
    /// на экране адреса показываются то в логических координатах, то в физических, и
    /// пересчёт при переключении не должен трогать сам список разделов.
    /// </summary>
    public sealed partial class PartitionRow : ObservableObject
    {
        [ObservableProperty] private bool selected = true;

        public int Number { get; init; }
        public string Name { get; init; } = "";
        public string Offset { get; init; } = "";
        public string Length { get; init; } = "";
        public string FileSystem { get; init; } = "";
        public string BadBlocks { get; init; } = "";
        public string Comment { get; init; } = "";

        /// <summary>Раздел, которому соответствует строка.</summary>
        public PartitionEntry Source { get; init; } = new();

        partial void OnSelectedChanged(bool value) => Source.Selected = value;
    }

    /// <summary>
    /// Вкладка «Извлечение разделов».
    ///
    /// Кроме обычной для вкладки работы — выбор файла, запуск, прогресс — здесь ещё и
    /// реализация <see cref="IAnalysisHost"/>: разбор дампа умеет писать в журнал и в двух
    /// местах обязан спросить человека. Ядро при этом ни диалогов, ни журнала не знает,
    /// поэтому мостиком служит эта ViewModel.
    /// </summary>
    public partial class ExtractViewModel : LogHostViewModel, IAnalysisHost
    {
        /// <summary>Найденные разделы — то, что показано в таблице.</summary>
        public ObservableCollection<PartitionRow> Partitions { get; } = new();

        [ObservableProperty] private string sourcePath = "";
        [ObservableProperty] private string outputPath = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AnalyseCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExtractCommand))]
        [NotifyCanExecuteChangedFor(nameof(SaveUdevCommand))]
        [NotifyCanExecuteChangedFor(nameof(SplitNandCommand))]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        private bool isBusy;

        [ObservableProperty] private double progress;
        [ObservableProperty] private string stageLabel = "";
        [ObservableProperty] private string summary = "";

        /// <summary>Есть ли что показывать и извлекать.</summary>
        [ObservableProperty] private bool hasResult;

        /// <summary>
        /// Есть ли что показать в «Общей информации». Пока файл не выбран — там подсказка;
        /// когда выбран, но не разобран, — его размер; после разбора — итог.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowInfoHint))]
        private bool hasInfo;

        public bool ShowInfoHint => !HasInfo;

        /// <summary>
        /// Показывать адреса так, как они лежат в дампе — вместе со служебными областями.
        /// Для eMMC переключатель бессмыслен и скрыт.
        /// </summary>
        [ObservableProperty] private bool showPhysicalAddresses;

        [ObservableProperty] private bool isNandDump;

        /// <summary>
        /// Дамп опознан как прошивка Philips, и для него есть осмысленное имя. Оригинал
        /// предлагал переименовать файл диалогом посреди разбора; здесь это отдельная
        /// кнопка — менять чужой файл программа должна только когда её об этом попросили.
        /// </summary>
        [ObservableProperty] private bool canRename;

        /// <summary>Имя, которое будет предложено при переименовании.</summary>
        [ObservableProperty] private string suggestedFileName = "";

        /// <summary>Искать ли файловые системы внутри разделов — ответ даётся заранее.</summary>
        [ObservableProperty] private bool searchFileSystems = TabOptionsService.Get(TabOptionsService.ExtractSearchFileSystems, true);

        // Оба параметра относятся к «Извлечь отмеченные» — единственной операции вкладки,
        // которая пишет столько, что место на диске стоит считать заранее, и оставляет
        // после себя папку, которую хочется открыть. Значения по умолчанию те же, что
        // в Конвертировании и Сборке.
        [ObservableProperty] private bool checkDiskSpace = TabOptionsService.Get(TabOptionsService.ExtractCheckDiskSpace, true);
        [ObservableProperty] private bool openFolderAfter = TabOptionsService.Get(TabOptionsService.ExtractOpenFolder, true);

        // Положение галочек переживает перезапуск (TabOptionsService); значения по
        // умолчанию остались прежними и заданы здесь же.
        partial void OnSearchFileSystemsChanged(bool value) => TabOptionsService.Set(TabOptionsService.ExtractSearchFileSystems, value);
        partial void OnCheckDiskSpaceChanged(bool value) => TabOptionsService.Set(TabOptionsService.ExtractCheckDiskSpace, value);
        partial void OnOpenFolderAfterChanged(bool value) => TabOptionsService.Set(TabOptionsService.ExtractOpenFolder, value);

        /// <summary>
        /// Поддерживает ли текущая работа паузу. Разбор дампа — нет: он читает файл
        /// короткими прыжками по адресам, приостанавливать там нечего и незачем.
        /// Извлечение и разделение пишут гигабайты подряд, и вот их остановить полезно —
        /// ровно как в «Конвертировании» и «Сборке файла», где кнопка паузы уже есть.
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        private bool supportsPause;

        [ObservableProperty] private bool isPaused;

        /// <summary>Подпись на кнопке паузы: её же меняет на «Возобновить».</summary>
        [ObservableProperty] private string pauseButtonText = Strings.Get("Common_PauseButton");

        public bool IsNotBusy => !IsBusy;

        public bool CanPause => IsBusy && SupportsPause;

        public bool CanAnalyse => !IsBusy && SourcePath.Length > 0;
        public bool CanUseResult => !IsBusy && HasResult;
        public bool CanSplitNand => !IsBusy && IsNandDump;

        /// <summary>
        /// Извлекать нечего, пока не отмечен ни один раздел. Отдельно от CanUseResult:
        /// сохранение .udev пишет таблицу целиком и от отметок не зависит.
        /// </summary>
        public bool CanExtract => CanUseResult && Partitions.Any(row => row.Selected);

        /// <summary>
        /// Общая отметка в шапке таблицы. Раньше на её месте были две кнопки, «Отметить
        /// все» и «Снять отметки»; галочка делает то же самое и заодно показывает,
        /// в каком состоянии таблица.
        ///
        /// Тип с null намеренно: три состояния — все отмечены, ни один не отмечен и
        /// «часть» (галочка рисуется чёрточкой). Само значение null галочке не выставить,
        /// у неё IsThreeState="False": щелчок переводит только между «все» и «ни одного»,
        /// а «часть» приходит от самих строк.
        /// </summary>
        public bool? AllSelected
        {
            get
            {
                if (Partitions.Count == 0) return false;

                int selected = Partitions.Count(row => row.Selected);
                if (selected == 0) return false;
                return selected == Partitions.Count ? true : null;
            }
            set
            {
                if (value.HasValue) SetAllSelected(value.Value);
            }
        }

        private CancellationTokenSource? _cts;

        /// <summary>
        /// Оценка «сколько осталось» под полосой прогресса. Часы отдельные, а не
        /// DateTime.Now в каждом отсчёте: разность двух моментов Stopwatch не зависит
        /// от того, перевели ли за это время системные часы.
        /// </summary>
        private readonly SpeedEstimator _speed = new();
        private readonly Stopwatch _clock = new();

        /// <summary>Название прохода, по которому сейчас идёт счёт, — чтобы заметить смену.</summary>
        private string _lastStage = "";

        /// <summary>
        /// Считает ли текущая операция байты. Если нет, показывать скорость нельзя:
        /// у разных проходов счёт идёт то в страницах, то в разделах.
        /// </summary>
        private bool _countsBytes;
        private readonly PauseController _pause = new();

        private PartitionAnalysisResult? _result;
        private PartitionTable _table = new();

        // Папку назначения человек мог задать сам, а мог оставить как есть. Пока она
        // «как есть», подставляем актуальный путь по умолчанию из «Настроек»: ViewModel
        // живёт дольше страницы, поэтому сам он не перечитается. Тот же приём и по той
        // же причине уже стоит в Конвертировании и Сборке.
        private bool _outputPathIsAuto = true;
        private bool _settingOutputPathInternally;

        public ExtractViewModel()
        {
            Progress = 0;
            _progress = new Progress<AnalysisProgress>(OnProgress);
            SetOutputPathAuto(OutputPathSettingsService.GetExtractFolder());
        }

        protected override void OnAttached()
        {
            if (_outputPathIsAuto) SetOutputPathAuto(OutputPathSettingsService.GetExtractFolder());

            // Файл мог смениться на диске, пока смотрели другой раздел.
            ScheduleSourceInfoUpdate();
        }

        private void SetOutputPathAuto(string path)
        {
            _settingOutputPathInternally = true;
            OutputPath = path;
            _settingOutputPathInternally = false;
        }

        partial void OnOutputPathChanged(string value)
        {
            if (!_settingOutputPathInternally) _outputPathIsAuto = false;
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanAnalyse));
            OnPropertyChanged(nameof(CanUseResult));
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanSplitNand));
            OnPropertyChanged(nameof(CanRenameDump));
            OnPropertyChanged(nameof(CanPause));
            RenameDumpCommand.NotifyCanExecuteChanged();
        }

        partial void OnSupportsPauseChanged(bool value) => OnPropertyChanged(nameof(CanPause));

        // Полоса на кнопке в панели задач показывает тот же общий ход работы, что и полоса
        // на странице, — чтобы за ним не приходилось разворачивать свёрнутое окно.
        partial void OnProgressChanged(double value) =>
            OperationLockService.Instance.Progress = value / 100.0;

        partial void OnIsPausedChanged(bool value)
        {
            OperationLockService.Instance.IsPaused = value;

            // На паузе оценка врёт: время идёт, а работа — нет. После возобновления она
            // посчитается заново, с чистого листа: иначе первые секунды показывали бы
            // скорость, размазанную по простою.
            if (value) _speed.Reset();
        }

        partial void OnSourcePathChanged(string value)
        {
            OnPropertyChanged(nameof(CanAnalyse));
            AnalyseCommand.NotifyCanExecuteChanged();

            // Переименование дампа — единственный случай, когда путь меняется, а файл
            // остаётся тем же: разбор к нему по-прежнему относится, и сбрасывать его
            // нельзя, иначе таблица разделов исчезала бы прямо после переименования.
            if (_keepAnalysisOnPathChange) return;

            // Про новый файл мы пока не знаем ничего: прежний размер в карточке был бы
            // не о нём. Настоящий придёт из ScheduleSourceInfoUpdate через четверть
            // секунды после того, как путь перестанут набирать.
            _sourceProbe = default;

            // Во всех остальных случаях путь показывает уже другой файл, а на экране
            // остаётся разбор прежнего. Это не только сбивало с толку: кнопка «Извлечь
            // отмеченные» оставалась доступной и читала бы НОВЫЙ файл по СТАРОЙ таблице
            // разделов — то есть молча резала бы его по чужим границам.
            ResetAnalysis();
            ScheduleSourceInfoUpdate();
        }

        /// <summary>
        /// Убирает с экрана всё, что осталось от предыдущего разбора. Вызывается и перед
        /// новым разбором, и при смене пути.
        /// </summary>
        private void ResetAnalysis()
        {
            _result = null;
            _table = new PartitionTable();

            HasResult = false;
            CanRename = false;
            SuggestedFileName = "";
            IsNandDump = false;
            ShowPhysicalAddresses = false;
            ClearRows();

            // Карточка возвращается к тому, что известно о файле без чтения, — к размеру.
            RenderSourceInfo();
        }

        // ---------- «Общая информация» до разбора ----------

        /// <summary>
        /// Как в «Конвертировании»: спрашивать файловую систему на каждую нажатую клавишу
        /// нельзя — на сетевом пути окно подвисало бы на каждую букву.
        /// </summary>
        private const int InputSettleDelayMs = 250;

        private int _probeGeneration;

        /// <summary>Последний ответ файловой системы о выбранном файле: из него собирается
        /// строка карточки, в том числе заново после смены языка.</summary>
        private FileProbeResult _sourceProbe;

        /// <summary>Путь меняет само переименование дампа — разбор при этом остаётся в силе.</summary>
        private bool _keepAnalysisOnPathChange;

        /// <summary>
        /// Каким файл был, когда его разбирали: размер и время записи. Нужны потому, что
        /// смена пути — не единственный способ подсунуть другой дамп. Путь может остаться
        /// прежним, а файл под ним — смениться: его перезаписали снаружи программы. Тогда
        /// ничего не происходит (свойство не менялось, сброс не срабатывает), и таблица
        /// разделов от прежнего содержимого молча применилась бы к новому.
        /// </summary>
        private FileProbeResult _analysedFile;

        /// <summary>
        /// Пересчёт с задержкой — для набора пути с клавиатуры. Счётчик поколений
        /// отбрасывает устаревшие ответы: пока ждали или ходили на диск, путь мог
        /// смениться ещё раз, и старый размер затёр бы новый.
        /// </summary>
        private async void ScheduleSourceInfoUpdate()
        {
            // Метод возвращает void — ждать его некому, и любое исключение отсюда стало бы
            // необработанным и уронило программу из-за одной строки в карточке.
            try
            {
                int generation = ++_probeGeneration;

                await Task.Delay(InputSettleDelayMs);
                if (generation != _probeGeneration) return;

                string path = SourcePath;
                var probe = await Task.Run(() => FileProbe.Measure(path));
                if (generation != _probeGeneration) return;

                _sourceProbe = probe;
                RenderSourceInfo();
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog",
                    nameof(ExtractViewModel), ex.GetType().Name, ex.Message));
            }
        }

        /// <summary>
        /// Что показывать в «Общей информации», пока разбора не было. Раньше там до самого
        /// нажатия «Начать» висела подсказка «выберите файл» — даже когда файл уже выбран.
        /// Размер — единственное, что о дампе известно без чтения, и его хватает, чтобы
        /// понять, что программа видит тот файл, который выбрали.
        /// </summary>
        private void RenderSourceInfo()
        {
            // После разбора карточка занята его итогом, и трогать её нельзя.
            if (_result is not null) return;

            if (!_sourceProbe.Exists)
            {
                HasInfo = false;
                Summary = "";
                return;
            }

            Summary = Strings.Format("Common_SourceSizeLine", SizeFormatHelper.Format(_sourceProbe.SizeBytes));
            HasInfo = true;
        }

        partial void OnHasResultChanged(bool value)
        {
            OnPropertyChanged(nameof(CanUseResult));
            OnPropertyChanged(nameof(CanExtract));
            ExtractCommand.NotifyCanExecuteChanged();
            SaveUdevCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsNandDumpChanged(bool value)
        {
            OnPropertyChanged(nameof(CanSplitNand));
            SplitNandCommand.NotifyCanExecuteChanged();
        }

        partial void OnCanRenameChanged(bool value) => RenameDumpCommand.NotifyCanExecuteChanged();

        /// <summary>Переключение координат перерисовывает таблицу, но не меняет разделы.</summary>
        partial void OnShowPhysicalAddressesChanged(bool value) => RebuildRows();

        // ---------- выбор файлов ----------

        [RelayCommand]
        private void BrowseSource()
        {
            var dlg = new OpenFileDialog { Filter = Strings.Get("Common_BinFileFilter") };
            if (dlg.ShowDialog() == true) SourcePath = dlg.FileName;
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true) OutputPath = dlg.FolderName;
        }

        // ---------- начало и конец любой длительной работы ----------

        /// <summary>
        /// Общее начало всех трёх длительных действий вкладки.
        ///
        /// Отдельно от самих команд — прежде всего из-за <see cref="OperationLockService"/>:
        /// вкладка о своей занятости не сообщала, хотя остальные три сообщают. Пока он не
        /// знает о работе, пункты меню остаются доступными, а кнопка установки обновления
        /// не блокируется — то есть посреди разбора многогигабайтного дампа можно было уйти
        /// на другую вкладку и запустить вторую операцию поверх первой или поставить
        /// установку обновления.
        /// </summary>
        /// <returns>
        /// Токен отмены — им и надо пользоваться дальше, а не полем <c>_cts</c>.
        /// Поле обнуляется в <see cref="EndOperation"/>, то есть к моменту, когда до него
        /// доберётся лямбда внутри <c>Task.Run</c>, там может уже не быть ничего. Читая
        /// поле, это ещё и не проходило проверку на null (CS8602): компилятор прав, такой
        /// код действительно небезопасен.
        /// </returns>
        private CancellationToken BeginOperation(bool supportsPause, bool countsBytes = false)
        {
            _cts = new CancellationTokenSource();
            IsBusy = true;
            SupportsPause = supportsPause;
            Progress = 0;

            _countsBytes = countsBytes;
            _lastStage = "";
            _speed.Reset();
            _clock.Restart();

            OperationLockService.Instance.OperationStarted(CancelNow);

            return _cts.Token;
        }

        /// <summary>
        /// Конец любого из них.
        ///
        /// Пауза снимается всегда: контроллер один на вкладку, и если работу отменили,
        /// не сняв паузу, следующая операция ушла бы в ожидание ещё до первого байта —
        /// без нажатой кнопки и без объяснения.
        /// </summary>
        private void EndOperation()
        {
            _pause.Resume();
            IsPaused = false;
            PauseButtonText = Strings.Get("Common_PauseButton");

            SupportsPause = false;
            IsBusy = false;
            StageLabel = "";
            Progress = 0;

            _cts?.Dispose();
            _cts = null;

            OperationLockService.Instance.OperationFinished();
        }

        /// <summary>
        /// Пауза и возобновление — одна кнопка, как в «Конвертировании» и «Сборке файла».
        /// Контроллер паузы вкладка передавала в ядро и раньше, но нажать её было негде:
        /// команды не существовало, и остановить извлечение можно было только отменой.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanPause))]
        private void TogglePause()
        {
            if (IsPaused) _pause.Resume();
            else _pause.Pause();

            IsPaused = _pause.IsPaused;
            PauseButtonText = Strings.Get(IsPaused ? "Common_ResumeButton" : "Common_PauseButton");
        }

        // ---------- разбор ----------

        [RelayCommand(CanExecute = nameof(CanAnalyse))]
        private async Task AnalyseAsync()
        {
            if (!File.Exists(SourcePath))
            {
                await DialogService.ShowErrorAsync(
                    Strings.Get("Common_FileNotFoundTitle"), Strings.Format("Common_FileNotFoundMessage", SourcePath));
                return;
            }

            // Разбор паузы не поддерживает — см. пояснение к SupportsPause.
            var ct = BeginOperation(supportsPause: false);

            // Прежний разбор с экрана убираем целиком, а не наполовину: в карточке
            // остаётся размер файла, чтобы во время работы она не показывала подсказку
            // «выберите файл» при уже выбранном.
            ResetAnalysis();

            // Примету файла запоминаем до чтения, а не после: разбор длится долго,
            // и подмена в это время должна считаться подменой.
            _analysedFile = FileProbe.Measure(SourcePath);

            try
            {
                AppLogger.Log(Strings.Format("Extract_AnalysisStarted", SourcePath));

                var result = await Task.Run(() => PartitionAnalysisOperation.RunAsync(
                    new PartitionAnalysisRequest { SourcePath = SourcePath }, this, ct));

                // Неопознанная разметка — тоже красная полоса: разбор гигабайтного дампа
                // идёт минутами, и «ничего не нашлось» надо увидеть, не разворачивая окно.
                OperationLockService.Instance.ReportResult(
                    result.Status is not (PartitionAnalysisStatus.Failed or PartitionAnalysisStatus.LayoutNotRecognised));

                ApplyResult(result);
                PrepareRename(result);
                await SaveArtifactsAsync(result, ct);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog", nameof(ExtractViewModel), ex.GetType().Name, ex.Message));
                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), ex.Message);
            }
            finally
            {
                EndOperation();
            }
        }

        private void ApplyResult(PartitionAnalysisResult result)
        {
            // Разбор состоялся: дальше в карточке идёт его итог — хоть разметка с числом
            // разделов, хоть сообщение об ошибке, — и подсказка больше не нужна.
            HasInfo = true;
            _result = result;
            _table = result.Table;
            IsNandDump = result.Geometry is not null;

            switch (result.Status)
            {
                case PartitionAnalysisStatus.SourceNotFound:
                case PartitionAnalysisStatus.Cancelled:
                case PartitionAnalysisStatus.Failed:
                case PartitionAnalysisStatus.EepromRecognised:
                case PartitionAnalysisStatus.LayoutNotRecognised:
                    Summary = BuildSummary(result);
                    return;
            }

            RebuildRows();
            HasResult = Partitions.Count > 0;

            Summary = BuildSummary(result);
        }

        /// <summary>
        /// Итог разбора одной строкой. Отдельным методом, потому что вызывается дважды:
        /// когда разбор закончился и заново при смене языка. Раньше пересборка была
        /// только для удачного разбора, и «Разметка не опознана» или «Отменено»
        /// оставались на прежнем языке.
        ///
        /// Единственное, что перевести нельзя, — <see cref="PartitionAnalysisResult.ErrorMessage"/>:
        /// его собирает Core в момент разбора, и без повторного разбора взять его
        /// на другом языке неоткуда.
        /// </summary>
        private string BuildSummary(PartitionAnalysisResult result) => result.Status switch
        {
            PartitionAnalysisStatus.SourceNotFound => Strings.Get("Common_FileNotFoundTitle"),
            PartitionAnalysisStatus.Cancelled => Strings.Get("Common_CancelledTitle"),
            PartitionAnalysisStatus.Failed => result.ErrorMessage,
            PartitionAnalysisStatus.EepromRecognised =>
                Strings.Format("Extract_PhilipsEeprom", result.Eeprom!.Model, result.Eeprom.Serial),
            PartitionAnalysisStatus.LayoutNotRecognised => Strings.Get("Extract_LayoutNotRecognised"),
            _ => Strings.Format("Extract_ResultSummary", result.MarkName ?? Strings.Get("Extract_UnknownMark"), Partitions.Count)
        };

        /// <summary>
        /// Побочные находки — build.prop и файл лицензии Dune HD — сохраняются рядом с
        /// дампом сразу: они маленькие, а искали их именно ради того, чтобы получить.
        /// </summary>
        private async Task SaveArtifactsAsync(PartitionAnalysisResult result, CancellationToken ct)
        {
            // Отчёт о разборе (analysis.json) пишется и тогда, когда находок не было:
            // он сам по себе находка — пара килобайт, по которым видно, что программа
            // увидела в дампе, вместо самого дампа на несколько гигабайт.
            bool reportWorthWriting =
                result.Status is PartitionAnalysisStatus.Completed or PartitionAnalysisStatus.LayoutNotRecognised;

            if (result.Android is null && !result.DuneLicense.Found && !reportWorthWriting) return;

            await AnalysisArtifactWriter.WriteAsync(result, SourcePath, OutputPath, this, ct);
        }

        /// <summary>Готовит кнопку переименования, если дамп опознан как прошивка Philips.</summary>
        private void PrepareRename(PartitionAnalysisResult result)
        {
            SuggestedFileName = result.Philips?.SuggestedFileName
                                ?? result.Eeprom?.SuggestedFileName
                                ?? "";

            CanRename = SuggestedFileName.Length > 0
                        && !string.Equals(SuggestedFileName, Path.GetFileName(SourcePath), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Очищает таблицу, отписываясь от строк. Подписка нужна ради кнопки «Извлечь
        /// отмеченные»: она гаснет, когда не отмечено ни одного раздела, а узнать об
        /// этом можно только от самой строки — коллекция о смене галочки не сообщает.
        /// </summary>
        private void ClearRows()
        {
            foreach (var row in Partitions) row.PropertyChanged -= OnRowChanged;

            Partitions.Clear();
            NotifySelectionChanged();
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PartitionRow.Selected)) NotifySelectionChanged();
        }

        private void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(AllSelected));
            ExtractCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Перестраивает таблицу из текущего списка разделов.</summary>
        private void RebuildRows()
        {
            var selection = Partitions.ToDictionary(r => r.Number, r => r.Selected);
            ClearRows();

            var geometry = _result?.Geometry;
            bool physical = ShowPhysicalAddresses && geometry is not null;

            // Адреса дополняются нулями до одной ширины, чтобы столбцы читались сверху
            // вниз, — так же, как их печатал оригинал. Ширина берётся по размеру дампа:
            // у восьмигигабайтного она больше, чем у стомегабайтного.
            string format = "X" + HexWidth(physical).ToString(CultureInfo.InvariantCulture);

            int number = 1;
            foreach (var part in _table.Items)
            {
                var shown = physical && part.Length >= 0 ? part.ToPhysical(geometry!) : part;

                Partitions.Add(new PartitionRow
                {
                    Number = number,
                    Name = part.Name,
                    Offset = "0x" + shown.Offset.ToString(format, CultureInfo.InvariantCulture),
                    Length = "0x" + shown.Length.ToString(format, CultureInfo.InvariantCulture),
                    FileSystem = FileSystemLabel(part.FsType),
                    BadBlocks = part.BadBlocks > 0 ? part.BadBlocks.ToString() : "",
                    Comment = part.Comment,
                    Source = part,
                    Selected = selection.TryGetValue(number, out bool wasSelected) ? wasSelected : part.Selected
                });

                number++;
            }

            // Слушаем каждую строку: от снятой галочки зависит доступность кнопки
            // «Извлечь отмеченные».
            foreach (var row in Partitions) row.PropertyChanged += OnRowChanged;

            NotifySelectionChanged();
        }

        /// <summary>
        /// Сколько шестнадцатеричных знаков нужно, чтобы записать любой адрес этого дампа.
        /// Физические адреса длиннее логических, поэтому ширина считается по тем, которые
        /// сейчас показаны.
        /// </summary>
        private int HexWidth(bool physical)
        {
            long size = _result?.LogicalSize ?? 0;
            if (physical && _result?.Geometry is { } geometry) size = geometry.AddSpare(size);

            return DumpContext.HexWidthOf(size);
        }

        private static string FileSystemLabel(FsType type) => type switch
        {
            FsType.Ext4 => "ext4",
            FsType.SquashFs => "squashfs",
            FsType.Fat16 => "fat16",
            FsType.Fat32 => "fat32",
            FsType.RomFs => "romfs",
            FsType.CramFs => "cramfs",
            FsType.Vdfs => "vdfs",
            FsType.Tar => "tar",
            _ => ""
        };

        // ---------- действия над результатом ----------

        /// <summary>
        /// Тот ли это файл, который разбирали. Проверяется перед всем, что опирается
        /// на таблицу разделов: путь мог остаться прежним, а содержимое смениться —
        /// файл перезаписали снаружи программы, пока окно было открыто.
        /// Само сравнение — <see cref="FileProbeResult.SameFileAs"/>.
        /// </summary>
        private async Task<bool> SourceStillMatchesAnalysisAsync()
        {
            if (FileProbe.Measure(SourcePath).SameFileAs(_analysedFile)) return true;

            // Разбор больше не о чем: убираем его целиком, иначе на экране останется
            // таблица, которой человек снова попробует воспользоваться.
            ResetAnalysis();
            ScheduleSourceInfoUpdate();

            await DialogService.ShowWarningAsync(
                Strings.Get("Extract_SourceChangedTitle"), Strings.Get("Extract_SourceChangedMessage"));
            return false;
        }

        [RelayCommand(CanExecute = nameof(CanExtract))]
        private async Task ExtractAsync()
        {
            if (!await SourceStillMatchesAnalysisAsync()) return;

            // Извлечение — единственная операция вкладки, которая считает записанные байты
            // (см. PartitionExtractOperation): только у неё скорость под полосой осмысленна.
            var ct = BeginOperation(supportsPause: true, countsBytes: true);

            try
            {
                var request = new PartitionExtractRequest
                {
                    SourcePath = SourcePath,
                    OutputFolder = OutputPath,
                    Source = SearchFileSystems ? ExtractionSource.FileSystems : ExtractionSource.PartitionTable,
                    Geometry = _result?.Geometry,
                    CheckDiskSpace = CheckDiskSpace
                };

                var outcome = await Task.Run(() => PartitionExtractOperation.RunAsync(
                    request, _table, this, ct, _pause));

                // Красной полосой в панели задач отмечаем только то, что случилось само:
                // отмену человек и так помнит, а до начала работы окно ещё перед глазами.
                OperationLockService.Instance.ReportResult(
                    outcome.Status != PartitionExtractStatus.Failed);

                await ReportExtractOutcomeAsync(outcome);
            }
            finally
            {
                EndOperation();
            }
        }

        private async Task ReportExtractOutcomeAsync(PartitionExtractOutcome outcome)
        {
            switch (outcome.Status)
            {
                case PartitionExtractStatus.Completed:
                    await DialogService.ShowInfoAsync(
                        Strings.Get("Common_DoneTitle"),
                        Strings.Format("Extract_ExtractDone", outcome.ExtractedCount, outcome.OutputFolder));

                    // После окна, а не до: иначе проводник открывается поверх диалога,
                    // и человек читает итог уже из-под чужого окна.
                    if (OpenFolderAfter) ResultFolder.Open(outcome.OutputFolder);
                    break;

                case PartitionExtractStatus.NothingSelected:
                    await DialogService.ShowWarningAsync(
                        Strings.Get("Common_CannotStartTitle"), Strings.Get("Extract_NothingSelected"));
                    break;

                case PartitionExtractStatus.NotEnoughSpace:
                    await DialogService.ShowWarningAsync(
                        Strings.Get("Common_LowSpaceTitle"), outcome.ErrorMessage);
                    break;

                case PartitionExtractStatus.Cancelled:
                    break;

                default:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), outcome.ErrorMessage);
                    break;
            }
        }

        [RelayCommand(CanExecute = nameof(CanUseResult))]
        private async Task SaveUdevAsync()
        {
            if (!await SourceStillMatchesAnalysisAsync()) return;

            var dlg = new SaveFileDialog
            {
                Filter = Strings.Get("Extract_UdevFilter"),
                FileName = SuggestUdevName()
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                var geometry = _result?.Geometry;

                UdevWriter.Write(dlg.FileName, _table, geometry,
                    ShowPhysicalAddresses ? UdevWriter.AddressMode.Physical : UdevWriter.AddressMode.Logical);

                AppLogger.Log(Strings.Format("Extract_UdevSaved", dlg.FileName));
            }
            catch (Exception ex)
            {
                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), ex.Message);
            }
        }

        private string SuggestUdevName()
        {
            string prefix = SearchFileSystems ? "fileSystems" : "partitions";
            string suffix = IsNandDump && !ShowPhysicalAddresses ? "_without_spare" : "";

            return $"{prefix}{suffix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.udev";
        }

        [RelayCommand(CanExecute = nameof(CanSplitNand))]
        private async Task SplitNandAsync()
        {
            if (!await SourceStillMatchesAnalysisAsync()) return;

            var ct = BeginOperation(supportsPause: true);

            try
            {
                var outcome = await Task.Run(() => NandSplitOperation.RunAsync(
                    SourcePath, _result?.Geometry, OutputPath, this, ct, _pause));

                OperationLockService.Instance.ReportResult(outcome.Status != NandSplitStatus.Failed);

                if (outcome.Status == NandSplitStatus.Completed)
                {
                    await DialogService.ShowInfoAsync(
                        Strings.Get("Common_DoneTitle"),
                        Strings.Format("Extract_SplitDone", outcome.MainPath, outcome.SparePath));
                }
                else if (outcome.Status != NandSplitStatus.Cancelled)
                {
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), outcome.ErrorMessage);
                }
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// Переименовывает дамп в имя, собранное из модели, версии и серийного номера.
        /// Это единственное место, где программа меняет исходный файл, поэтому спрашивает
        /// подтверждение и никогда не перезаписывает существующий файл.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanRenameDump))]
        private async Task RenameDumpAsync()
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(SourcePath)) ?? ".";
            string target = Path.Combine(folder, SuggestedFileName);

            if (File.Exists(target))
            {
                await DialogService.ShowWarningAsync(
                    Strings.Get("Extract_RenameQuestionTitle"), Strings.Get("Extract_RenameExists"));
                return;
            }

            var answer = await DialogService.ShowConfirmAsync(
                Strings.Get("Extract_RenameQuestionTitle"),
                Strings.Format("Extract_RenameQuestion", SuggestedFileName),
                Strings.Get("Common_OkButton"), null, Strings.Get("Common_CancelButton"));

            if (answer != DialogChoice.Primary) return;

            try
            {
                File.Move(SourcePath, target);

                // Файл тот же, у него сменилось имя, — разбор остаётся в силе.
                _keepAnalysisOnPathChange = true;
                try { SourcePath = target; }
                finally { _keepAnalysisOnPathChange = false; }

                CanRename = false;

                AppLogger.Log(Strings.Format("Extract_RenameDone", target));
            }
            catch (Exception ex)
            {
                await DialogService.ShowErrorAsync(
                    Strings.Get("Common_Error"), Strings.Format("Extract_RenameFailed", ex.Message));
            }
        }

        public bool CanRenameDump => !IsBusy && CanRename;

        /// <summary>
        /// Спрашивает, прежде чем прервать. Сообщение общее для всех трёх работ вкладки:
        /// разбор ничего не пишет и терять там нечего, но извлечение и разделение NAND
        /// пишут гигабайты, а какая из трёх идёт сейчас, человек и так видит по подписи
        /// под полосой. Одно честное предупреждение лучше трёх разных.
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

        /// <summary>Прервать молча — этим же пользуется закрытие окна.</summary>
        private void CancelNow() => _cts?.Cancel();

        private void SetAllSelected(bool value)
        {
            foreach (var row in Partitions) row.Selected = value;
        }

        /// <summary>
        /// Тексты сводки и таблицы собраны кодом, поэтому сами они при смене языка не
        /// переведутся — их надо пересобрать. Разбирать дамп заново для этого не нужно:
        /// результат разбора от языка не зависит.
        /// </summary>
        protected override void OnLanguageChanged()
        {
            // Подпись кнопки паузы тоже собрана кодом, и её состояние надо сохранить:
            // если сейчас пауза, после смены языка должно остаться «Возобновить».
            PauseButtonText = Strings.Get(IsPaused ? "Common_ResumeButton" : "Common_PauseButton");

            if (_result is null)
            {
                RenderSourceInfo();
                return;
            }

            // Таблицу пересобираем только когда она есть, а сводку — всегда: у разбора,
            // который ничем не кончился (не опознан, отменён, EEPROM), она тоже своя.
            if (HasResult) RebuildRows();
            Summary = BuildSummary(_result);
        }

        // ---------- IAnalysisHost ----------

        void IAnalysisHost.Log(string message, AnalysisLogLevel level) => AppLogger.Log(message);

        /// <summary>
        /// Ответ на вопрос о поиске файловых систем даётся галочкой заранее, а не диалогом
        /// посреди работы. Оригинал спрашивал в середине разбора — в момент, когда человек
        /// уже отошёл от компьютера, потому что первый проход по большому дампу небыстрый.
        /// </summary>
        Task<bool> IAnalysisHost.ConfirmAsync(string question, CancellationToken ct) =>
            Task.FromResult(SearchFileSystems);

        /// <summary>
        /// Автоопределение размера страницы NAND не справилось. Список вариантов приходит
        /// из ядра, показать его должна вкладка.
        ///
        /// Спрашивают отсюда из фонового потока: разбор целиком идёт внутри Task.Run,
        /// и продолжения его await'ов остаются там же. А диалог — это окно WPF, которое
        /// в чужом потоке не построить: первым же падает обращение к Application.MainWindow
        /// (владелец окна), потому что Application живёт в потоке интерфейса и проверяет
        /// это на каждом обращении. Дальше не пустил бы и сам показ: модальное окно
        /// требует STA-потока, а поток из пула — MTA.
        ///
        /// Из-за этого вопрос о геометрии не появлялся вовсе: вместо списка вариантов
        /// разбор падал, и человек видел окно с сообщением об ошибке потока. Проверить
        /// это тестом нельзя — здесь та самая граница, за которой начинается WPF.
        ///
        /// Поэтому окно открывается в потоке интерфейса, а дожидаться ответа можно откуда
        /// угодно: наружу уходит Task, и await над ним возвращается в фоновый поток сам.
        /// </summary>
        async Task<int?> IAnalysisHost.AskNandGeometryAsync(IReadOnlyList<NandGeometryOption> options, CancellationToken ct)
        {
            var lines = options.Select(o => o.IsNoSpare
                ? Strings.Format("Extract_GeometryOptionNoSpare", o.Index)
                : Strings.Format("Extract_GeometryOption", o.Index, o.Main, o.Spare)).ToList();

            Task<int?> Ask() => DialogService.AskChoiceAsync(
                Strings.Get("Extract_GeometryQuestionTitle"),
                Strings.Get("Extract_GeometryQuestion"),
                lines);

            var dispatcher = Application.Current?.Dispatcher;

            // Уже в потоке интерфейса (или программа запущена без Application — так
            // бывает в тестовом окружении): звать через диспетчер незачем.
            if (dispatcher is null || dispatcher.CheckAccess()) return await Ask();

            return await dispatcher.InvokeAsync(Ask).Task.Unwrap();
        }

        IProgress<AnalysisProgress>? IAnalysisHost.Progress => _progress;

        /// <summary>
        /// Куда ядро сообщает о ходе работы.
        ///
        /// Создаётся здесь, в поле, а не при первом обращении — и это важно, где именно.
        /// Progress&lt;T&gt; запоминает поток в момент своего создания и потом возвращает
        /// вызовы туда же. Создавался он лениво, а первым спрашивал его как раз разбор —
        /// то есть из потока пула, где запоминать нечего: обработчик выполнялся где
        /// придётся, а изменения свойств доезжали до окна окольным путём, через разбор
        /// привязок. Поле инициализируется при создании ViewModel, а её создаёт страница
        /// в потоке интерфейса — значит и OnProgress выполняется там же, всегда.
        /// </summary>
        private readonly Progress<AnalysisProgress> _progress;

        private void OnProgress(AnalysisProgress value)
        {
            Progress = value.Total > 0 ? Math.Clamp(value.Done * 100.0 / value.Total, 0, 100) : 0;

            // Проходов у разбора много, и каждый идёт со своей скоростью: накопленное
            // на предыдущем к следующему не относится совсем, а счётчик у нового прохода
            // и вовсе начинается с нуля.
            if (!string.Equals(value.Stage, _lastStage, StringComparison.Ordinal))
            {
                _lastStage = value.Stage;
                _speed.Reset();
                _clock.Restart();
            }

            _speed.Add(_clock.Elapsed, value.Done);

            // Скорость показываем только там, где счёт идёт по байтам. У проходов разбора
            // это то страницы, то разделы, и «180 МБ/с» на них было бы просто неправдой.
            // А вот оценка оставшегося времени от единиц не зависит: доля есть доля.
            StageLabel = ProgressCaption.Build(
                value.Stage,
                _countsBytes ? _speed.BytesPerSecond : null,
                _speed.Remaining(value.Total));
        }
    }
}
