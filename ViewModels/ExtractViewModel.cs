using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private bool isBusy;

        [ObservableProperty] private double progress;
        [ObservableProperty] private string stageLabel = "";
        [ObservableProperty] private string summary = "";

        /// <summary>Есть ли что показывать и извлекать.</summary>
        [ObservableProperty] private bool hasResult;

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

        public bool IsNotBusy => !IsBusy;

        public bool CanAnalyse => !IsBusy && SourcePath.Length > 0;
        public bool CanUseResult => !IsBusy && HasResult;
        public bool CanSplitNand => !IsBusy && IsNandDump;

        private CancellationTokenSource? _cts;
        private readonly PauseController _pause = new();

        private PartitionAnalysisResult? _result;
        private PartitionTable _table = new();

        public ExtractViewModel()
        {
            Progress = 0;
            Summary = Strings.Get("Extract_NoResultYet");
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanAnalyse));
            OnPropertyChanged(nameof(CanUseResult));
            OnPropertyChanged(nameof(CanSplitNand));
            OnPropertyChanged(nameof(CanRenameDump));
            RenameDumpCommand.NotifyCanExecuteChanged();
        }

        partial void OnSourcePathChanged(string value)
        {
            OnPropertyChanged(nameof(CanAnalyse));
            AnalyseCommand.NotifyCanExecuteChanged();
        }

        partial void OnHasResultChanged(bool value)
        {
            OnPropertyChanged(nameof(CanUseResult));
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

            _cts = new CancellationTokenSource();
            IsBusy = true;
            HasResult = false;
            CanRename = false;
            SuggestedFileName = "";
            Partitions.Clear();
            Progress = 0;

            try
            {
                AppLogger.Log(Strings.Format("Extract_AnalysisStarted", SourcePath));

                var result = await Task.Run(() => PartitionAnalysisOperation.RunAsync(
                    new PartitionAnalysisRequest { SourcePath = SourcePath }, this, _cts.Token));

                ApplyResult(result);
                PrepareRename(result);
                await SaveArtifactsAsync(result, _cts.Token);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog", nameof(ExtractViewModel), ex.GetType().Name, ex.Message));
                await DialogService.ShowErrorAsync(Strings.Get("Common_Error"), ex.Message);
            }
            finally
            {
                IsBusy = false;
                StageLabel = "";
                Progress = 0;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void ApplyResult(PartitionAnalysisResult result)
        {
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

            Summary = Strings.Format("Extract_ResultSummary", result.MarkName ?? "—", Partitions.Count);
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

        /// <summary>Перестраивает таблицу из текущего списка разделов.</summary>
        private void RebuildRows()
        {
            var selection = Partitions.ToDictionary(r => r.Number, r => r.Selected);
            Partitions.Clear();

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

        [RelayCommand(CanExecute = nameof(CanUseResult))]
        private async Task ExtractAsync()
        {
            _cts = new CancellationTokenSource();
            IsBusy = true;

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
                    request, _table, this, _cts.Token, _pause));

                await ReportExtractOutcomeAsync(outcome);
            }
            finally
            {
                IsBusy = false;
                StageLabel = "";
                Progress = 0;
                _cts?.Dispose();
                _cts = null;
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
            _cts = new CancellationTokenSource();
            IsBusy = true;

            try
            {
                var outcome = await Task.Run(() => NandSplitOperation.RunAsync(
                    SourcePath, _result?.Geometry, OutputPath, this, _cts.Token, _pause));

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
                IsBusy = false;
                StageLabel = "";
                Progress = 0;
                _cts?.Dispose();
                _cts = null;
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

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void SelectAll() => SetAllSelected(true);

        [RelayCommand]
        private void SelectNone() => SetAllSelected(false);

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
            if (_result is null)
            {
                Summary = Strings.Get("Extract_NoResultYet");
                return;
            }

            if (HasResult)
            {
                RebuildRows();
                Summary = Strings.Format("Extract_ResultSummary", _result.MarkName ?? "—", Partitions.Count);
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
