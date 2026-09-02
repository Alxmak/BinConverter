using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Operations;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// Строка таблицы «Что войдёт в файл». Пустые промежутки показываются наравне
    /// с разделами: человек должен видеть, что часть образа возьмётся не из файлов,
    /// а из заполнителя.
    /// </summary>
    public sealed class PartitionMergeRow
    {
        public int Number { get; init; }
        public string Offset { get; init; } = "";
        public string Length { get; init; } = "";
        public string Name { get; init; } = "";
    }

    /// <summary>
    /// Вкладка «Объединение разделов» — действие, обратное извлечению.
    ///
    /// Собирать умеет папку, в которую извлекались разделы: адрес и размер каждого куска
    /// записаны в его имени, поэтому ни таблицы, ни .udev рядом держать не нужно. Вся
    /// проверка сходимости — в <see cref="PartitionMergePlan"/>, здесь только показ
    /// и диалоги.
    /// </summary>
    public partial class PartitionMergeViewModel : OperationTabViewModel
    {
        /// <summary>Что ляжет в образ: куски и промежутки между ними, по возрастанию адреса.</summary>
        public ObservableCollection<PartitionMergeRow> Rows { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStart))]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        private string sourceFolder = "";

        [ObservableProperty] private string outputPath = "";

        /// <summary>Сведения о будущем образе: сколько разделов, какой размер, что мешает.</summary>
        [ObservableProperty] private string generalInfoText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowInfoHint))]
        private bool hasInfo;

        public bool ShowInfoHint => !HasInfo;

        /// <summary>Есть ли что показывать в таблице.</summary>
        [ObservableProperty] private bool hasRows;

        // Положение галочек переживает перезапуск (TabOptionsService). Значения
        // по умолчанию заданы здесь же, как и в остальных вкладках.
        [ObservableProperty] private bool fillWithZeros = TabOptionsService.Get(TabOptionsService.PartitionMergeFillZeros, false);
        [ObservableProperty] private bool checkDiskSpace = TabOptionsService.Get(TabOptionsService.PartitionMergeCheckDiskSpace, true);
        [ObservableProperty] private bool openFolderAfter = TabOptionsService.Get(TabOptionsService.PartitionMergeOpenFolder, true);

        partial void OnCheckDiskSpaceChanged(bool value) => TabOptionsService.Set(TabOptionsService.PartitionMergeCheckDiskSpace, value);
        partial void OnOpenFolderAfterChanged(bool value) => TabOptionsService.Set(TabOptionsService.PartitionMergeOpenFolder, value);

        partial void OnFillWithZerosChanged(bool value)
        {
            TabOptionsService.Set(TabOptionsService.PartitionMergeFillZeros, value);

            // В карточке написано, чем заполнятся пустоты, — значит от галочки зависит
            // не только запись, но и то, что человек читает до неё.
            RenderPlan();
        }

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";
        [ObservableProperty] private double currentFileProgress;

        /// <summary>
        /// Собирать можно, когда папка осмотрена и куски в ней сходятся. Пока план
        /// не посчитан или в нём есть замечания, кнопка гаснет: узнавать о том, что
        /// разделы не складываются, лучше до записи многогигабайтного файла.
        /// </summary>
        public bool CanStart => !IsBusy && SourceFolder.Length > 0 && _plan is { CanMerge: true };

        /// <summary>Сколько ждать после последнего нажатия, прежде чем идти на диск.</summary>
        private const int InputSettleDelayMs = 250;

        private const string DefaultOutputFileName = "partitions_merged.bin";

        private int _scanGeneration;
        private PartitionMergePlan? _plan;

        // Папку результата человек мог задать сам, а мог оставить как есть — тот же приём
        // и по той же причине, что в «Конвертировании», «Сборке» и «Извлечении».
        private bool _outputPathIsAuto = true;
        private bool _settingOutputPathInternally;

        public PartitionMergeViewModel() => ResetOutputPathToDefault();

        protected override void OnAttached()
        {
            if (_outputPathIsAuto) ResetOutputPathToDefault();

            // Папку могли пополнить или почистить, пока смотрели другой раздел.
            ScanNow();
        }

        protected override void OnLanguageChanged()
        {
            base.OnLanguageChanged();

            // Карточка и таблица собраны кодом, поэтому сами не переведутся. Осматривать
            // папку заново для этого не нужно: план от языка не зависит.
            RenderPlan();
        }

        protected override void OnBusyChanged(bool busy)
        {
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Подпись под полосой «Текущий файл» — там же идёт и оценка.</summary>
        protected override void ApplyCaption(string text) => CurrentFileLabel = text;

        partial void OnOverallProgressChanged(double value) => ReportTaskbarProgress(value);

        partial void OnSourceFolderChanged(string value) => ScheduleScan();

        partial void OnOutputPathChanged(string value)
        {
            if (!_settingOutputPathInternally) _outputPathIsAuto = false;
        }

        // ---------- выбор путей ----------

        [RelayCommand]
        private void BrowseSource()
        {
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true) SourceFolder = dlg.FolderName;
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            var dlg = new SaveFileDialog { Filter = Strings.Get("Common_BinFileFilter"), FileName = DefaultOutputFileName };
            if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
        }

        /// <summary>
        /// Меняет только папку результата, имя файла оставляет прежним: сюда перетаскивают
        /// папку, а путь здесь — файл. То же, что в «Сборке файла».
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

        private void ResetOutputPathToDefault() =>
            SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), DefaultOutputFileName));

        /// <summary>
        /// Имя результата по имени папки: разделы обычно лежат в папке, названной по дампу,
        /// и «emmc_parts_2026-01-01.bin» понятнее, чем общее «partitions_merged.bin».
        /// </summary>
        private void SuggestOutputPath(string folder)
        {
            if (!_outputPathIsAuto) return;

            string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string fileName = name.Length > 0 ? name + ".bin" : DefaultOutputFileName;

            SetOutputPathAuto(Path.Combine(OutputPathSettingsService.GetMergeFolder(), fileName));
        }

        // ---------- осмотр папки ----------

        /// <summary>
        /// Осмотр с задержкой — для набора пути с клавиатуры. Как и в остальных вкладках,
        /// на диск ходим только из фонового потока: в папке разделов сотни файлов,
        /// а на сетевом пути каждый ответ ждёт своего таймаута.
        /// </summary>
        private void ScheduleScan() => Scan(InputSettleDelayMs);

        private void ScanNow() => Scan(delayMs: 0);

        private async void Scan(int delayMs)
        {
            // Метод возвращает void — ждать его некому, и необработанное исключение отсюда
            // уронило бы программу из-за осмотра папки.
            try
            {
                int generation = ++_scanGeneration;

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                    if (generation != _scanGeneration) return;
                }

                string folder = SourceFolder;
                var plan = await Task.Run(() => PartitionMergePlan.Build(folder));
                if (generation != _scanGeneration) return;

                _plan = plan;

                RenderPlan();
                NotifyCanStartChanged();

                if (plan.Pieces.Count > 0) SuggestOutputPath(folder);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog",
                    nameof(PartitionMergeViewModel), ex.GetType().Name, ex.Message));
            }
        }

        private void NotifyCanStartChanged()
        {
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Что показать о плане. Вызывается и после осмотра, и при смене языка.</summary>
        private void RenderPlan()
        {
            if (_plan is not { } plan || SourceFolder.Length == 0)
            {
                HasInfo = false;
                GeneralInfoText = "";
                Rows.Clear();
                HasRows = false;
                return;
            }

            var text = new StringBuilder();

            if (plan.Pieces.Count > 0)
            {
                text.AppendLine(Strings.Format("PartitionMerge_PiecesFoundLine",
                    plan.Pieces.Count, SizeFormatHelper.Format(plan.DataSize)));
                text.AppendLine(Strings.Format("Common_ResultSizeLine", SizeFormatHelper.Format(plan.TotalSize)));

                if (plan.Gaps.Count > 0)
                {
                    text.AppendLine(Strings.Format("PartitionMerge_GapsLine",
                        plan.Gaps.Count,
                        SizeFormatHelper.Format(plan.GapSize),
                        Strings.Get(FillWithZeros ? "PartitionMerge_FillerZero" : "PartitionMerge_FillerFf")));
                }
            }

            // Пропущенные файлы — не ошибка: в папке разделов рядом лежат и analysis.json,
            // и .udev. Но сказать, сколько файлов прошло мимо, надо: иначе пропущенный
            // из-за опечатки в имени раздел заметить нечем.
            if (plan.SkippedFiles.Count > 0)
                text.AppendLine(Strings.Format("PartitionMerge_SkippedLine", plan.SkippedFiles.Count));

            if (plan.Issues.Count > 0)
            {
                text.AppendLine(Strings.Get("PartitionMerge_CannotMergeHeader"));
                foreach (var issue in plan.Issues) text.AppendLine(issue.Describe());
            }

            GeneralInfoText = text.ToString().TrimEnd();
            HasInfo = GeneralInfoText.Length > 0;

            RebuildRows(plan);
        }

        private void RebuildRows(PartitionMergePlan plan)
        {
            Rows.Clear();

            if (plan.Pieces.Count == 0)
            {
                HasRows = false;
                return;
            }

            // Адреса дополняются нулями до одной ширины — так же, как в таблице разбора:
            // столбец читается сверху вниз, а не выравнивается по-разному в каждой строке.
            string format = "X" + DumpContext.HexWidthOf(plan.TotalSize).ToString(CultureInfo.InvariantCulture);

            var rows = new List<(long Offset, long Length, string Name)>();

            foreach (var piece in plan.Pieces) rows.Add((piece.Offset, piece.Length, piece.Name));
            foreach (var gap in plan.Gaps) rows.Add((gap.Offset, gap.Length, Strings.Get("PartitionMerge_GapRowName")));

            rows.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            int number = 1;
            foreach (var row in rows)
            {
                Rows.Add(new PartitionMergeRow
                {
                    Number = number++,
                    Offset = "0x" + row.Offset.ToString(format, CultureInfo.InvariantCulture),
                    Length = "0x" + row.Length.ToString(format, CultureInfo.InvariantCulture),
                    Name = row.Name
                });
            }

            HasRows = true;
        }

        // ---------- работа ----------

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            var request = new PartitionMergeRequest
            {
                SourceFolder = SourceFolder,
                OutputPath = OutputPath,
                FillWithZeros = FillWithZeros,
                CheckDiskSpace = CheckDiskSpace
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
                var outcome = await PartitionMergeOperation.RunAsync(
                    request, new DialogConflictResolver(), progress,
                    AppLogger.Log, Pause, ct, MarkOperationStarted);

                // При конфликте операция могла уйти в соседнее имя — показываем, куда именно.
                if (outcome.OutputPathChanged) OutputPath = outcome.OutputPath;

                // Красной полосой в панели задач отмечаем только то, что случилось само:
                // отказы до начала работы человек читает в окне, которое ещё перед ним.
                OperationLockService.Instance.ReportResult(outcome.Status != PartitionMergeStatus.Failed);

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

        private async Task ShowOutcomeAsync(PartitionMergeOutcome outcome)
        {
            switch (outcome.Status)
            {
                case PartitionMergeStatus.SourceFolderNotFound:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Get("PartitionMerge_SelectFolderFirst"));
                    break;

                case PartitionMergeStatus.SourceFolderNotUsable:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"),
                        Strings.Format("PartitionMerge_FolderNotUsableMessage", outcome.ErrorMessage));
                    break;

                case PartitionMergeStatus.OutputPathNotSpecified:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Get("Common_SpecifyOutputFile"));
                    break;

                case PartitionMergeStatus.NothingToMerge:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Get("PartitionMerge_NothingToMergeMessage"));
                    break;

                // Замечания к плану — это отказ до начала работы: собранный не из того
                // образ выглядел бы настоящим.
                case PartitionMergeStatus.LayoutBroken:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Format("PartitionMerge_LayoutBrokenMessage",
                            string.Join("\n", outcome.Plan.Issues.Select(issue => issue.Describe()))));
                    break;

                case PartitionMergeStatus.OutputInsideSource:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Get("PartitionMerge_OutputInsideSourceMessage"));
                    break;

                case PartitionMergeStatus.OutputNotUsable:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Format("Common_OutputNotUsableMessage", outcome.ErrorMessage));
                    break;

                case PartitionMergeStatus.NotEnoughSpace:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_LowSpaceTitle"),
                        Strings.Format("Common_LowSpaceMessage",
                            SizeFormatHelper.Format(outcome.Plan.TotalSize),
                            SizeFormatHelper.Format(outcome.SpaceCheck.RequiredBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.AvailableBytes),
                            SizeFormatHelper.Format(outcome.SpaceCheck.MissingBytes)));
                    break;

                case PartitionMergeStatus.CancelledBeforeStart:
                    // Человек сам отказался перезаписывать файл — сообщать ему об этом нечего.
                    break;

                case PartitionMergeStatus.Completed:
                    // Хэш собранного образа — то, ради чего сборку и проверяют: его сверяют
                    // с хэшем исходного дампа, и переписывать 64 знака с экрана незачем.
                    await DialogService.ShowInfoWithHashesAsync(Strings.Get("Common_DoneTitle"),
                        Strings.Format("PartitionMerge_DoneMessage", outcome.PiecesUsed, outcome.TotalBytes),
                        new DialogService.HashRow(Strings.Get("Common_HashResultLabel"), outcome.ResultHash));

                    if (OpenFolderAfter) OpenResultFolder(outcome.OutputPath);
                    break;

                case PartitionMergeStatus.Cancelled:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_CancelledTitle"),
                        Strings.Get("PartitionMerge_CancelledMessage"));
                    break;

                case PartitionMergeStatus.Failed:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), outcome.DiskFull
                        ? Strings.Get("Common_DiskFullMessage")
                        : Strings.Format("PartitionMerge_ErrorMessage", outcome.ErrorMessage));
                    break;
            }
        }

        private static void OpenResultFolder(string outputPath)
        {
            string? folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(folder)) ResultFolder.Open(folder);
        }
    }
}
