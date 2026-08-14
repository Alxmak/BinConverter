using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        /// Был ли разбор. Пока его не было, «Общая информация» показывает подсказку, а не
        /// строку итога: до разбора итог всё равно ничего не сообщает о дампе.
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
        [ObservableProperty] private bool searchFileSystems = true;

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
            Summary = Strings.Get("Extract_NoResultYet");
            SetOutputPathAuto(OutputPathSettingsService.GetExtractFolder());
        }

        protected override void OnAttached()
        {
            if (_outputPathIsAuto) SetOutputPathAuto(OutputPathSettingsService.GetExtractFolder());
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

        partial void OnSourcePathChanged(string value)
        {
            OnPropertyChanged(nameof(CanAnalyse));
            AnalyseCommand.NotifyCanExecuteChanged();
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
        private CancellationToken BeginOperation(bool supportsPause)
        {
            _cts = new CancellationTokenSource();
            IsBusy = true;
            SupportsPause = supportsPause;
            Progress = 0;

            OperationLockService.Instance.IsBusy = true;

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

            OperationLockService.Instance.IsBusy = false;
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

            HasResult = false;
            CanRename = false;
            SuggestedFileName = "";
            ClearRows();

            try
            {
                AppLogger.Log(Strings.Format("Extract_AnalysisStarted", SourcePath));

                var result = await Task.Run(() => PartitionAnalysisOperation.RunAsync(
                    new PartitionAnalysisRequest { SourcePath = SourcePath }, this, ct));

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
                    Summary = Strings.Get("Common_FileNotFoundTitle");
                    return;

                case PartitionAnalysisStatus.Cancelled:
                    Summary = Strings.Get("Common_CancelledTitle");
                    return;

                case PartitionAnalysisStatus.Failed:
                    Summary = result.ErrorMessage;
                    return;

                case PartitionAnalysisStatus.EepromRecognised:
                    Summary = Strings.Format("Extract_PhilipsEeprom", result.Eeprom!.Model, result.Eeprom.Serial);
                    return;

                case PartitionAnalysisStatus.LayoutNotRecognised:
                    Summary = Strings.Get("Extract_LayoutNotRecognised");
                    return;
            }

            RebuildRows();
            HasResult = Partitions.Count > 0;

            Summary = Strings.Format("Extract_ResultSummary", result.MarkName ?? Strings.Get("Extract_UnknownMark"), Partitions.Count);
        }

        /// <summary>
        /// Побочные находки — build.prop и файл лицензии Dune HD — сохраняются рядом с
        /// дампом сразу: они маленькие, а искали их именно ради того, чтобы получить.
        /// </summary>
        private async Task SaveArtifactsAsync(PartitionAnalysisResult result, CancellationToken ct)
        {
            if (result.Android is null && !result.DuneLicense.Found) return;

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

        [RelayCommand(CanExecute = nameof(CanExtract))]
        private async Task ExtractAsync()
        {
            var ct = BeginOperation(supportsPause: true);

            try
            {
                var request = new PartitionExtractRequest
                {
                    SourcePath = SourcePath,
                    OutputFolder = OutputPath,
                    Source = SearchFileSystems ? ExtractionSource.FileSystems : ExtractionSource.PartitionTable,
                    Geometry = _result?.Geometry,
                    CheckDiskSpace = true
                };

                var outcome = await Task.Run(() => PartitionExtractOperation.RunAsync(
                    request, _table, this, ct, _pause));

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
            var ct = BeginOperation(supportsPause: true);

            try
            {
                var outcome = await Task.Run(() => NandSplitOperation.RunAsync(
                    SourcePath, _result?.Geometry, OutputPath, this, ct, _pause));

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
                SourcePath = target;
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

        [RelayCommand(CanExecute = nameof(IsBusy))]
        private void Cancel() => _cts?.Cancel();

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
                Summary = Strings.Get("Extract_NoResultYet");
                return;
            }

            if (HasResult)
            {
                RebuildRows();
                Summary = Strings.Format("Extract_ResultSummary", _result.MarkName ?? Strings.Get("Extract_UnknownMark"), Partitions.Count);
            }
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
        /// </summary>
        async Task<int?> IAnalysisHost.AskNandGeometryAsync(IReadOnlyList<NandGeometryOption> options, CancellationToken ct)
        {
            var lines = options.Select(o => o.IsNoSpare
                ? Strings.Format("Extract_GeometryOptionNoSpare", o.Index)
                : Strings.Format("Extract_GeometryOption", o.Index, o.Main, o.Spare));

            return await DialogService.AskChoiceAsync(
                Strings.Get("Extract_GeometryQuestionTitle"),
                Strings.Get("Extract_GeometryQuestion"),
                lines.ToList());
        }

        IProgress<AnalysisProgress>? IAnalysisHost.Progress => _progress ??= new Progress<AnalysisProgress>(OnProgress);

        private IProgress<AnalysisProgress>? _progress;

        private void OnProgress(AnalysisProgress value)
        {
            StageLabel = value.Stage;
            Progress = value.Total > 0 ? Math.Clamp(value.Done * 100.0 / value.Total, 0, 100) : 0;
        }
    }
}
