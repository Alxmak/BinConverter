using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    /// <summary>Одна строка выбора файла в разделе сравнения.</summary>
    public sealed partial class VerifyFileSlot : ObservableObject
    {
        [ObservableProperty] private string label = "";
        [ObservableProperty] private string path = "";
    }

    /// <summary>
    /// Одна строка в карточке результата: заголовок, хэш и имена файлов под ним.
    /// Что за строка — зависит от числа сравниваемых файлов: при двух это сам файл
    /// («Файл 1»), при трёх и больше — группа совпавших хэшей.
    /// </summary>
    public sealed class VerifyResultRow
    {
        public string Title { get; init; } = "";
        public string Hash { get; init; } = "";

        /// <summary>Имена файлов строки, по одному в строке.</summary>
        public string FileNames { get; init; } = "";
    }

    public partial class VerifyViewModel : LogHostViewModel
    {
        /// <summary>Строки выбора файлов. Их число меняется кнопками «Добавить»/«Убрать».</summary>
        public ObservableCollection<VerifyFileSlot> Files { get; } = new();

        /// <summary>Что показывается в карточке результата: строка на файл при двух
        /// файлах, строка на группу совпавших хэшей при трёх и больше.</summary>
        public ObservableCollection<VerifyResultRow> ResultRows { get; } = new();

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(AddFileCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveFileCommand))]
        // Без этой строки «Отмена» не включалась никогда. RelayCommand из
        // CommunityToolkit не слушает CommandManager.RequerySuggested, как обычные
        // команды WPF: пока не позвать NotifyCanExecuteChanged, кнопка остаётся с тем
        // CanExecute, что был посчитан при создании, — а при создании IsBusy равен false.
        // На остальных вкладках эта строка есть, здесь её забыли.
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        private bool isBusy;

        [ObservableProperty] private bool hasResult;

        /// <summary>Все файлы совпали — от этого зависит цвет баннера результата.</summary>
        [ObservableProperty] private bool isResultGood;

        [ObservableProperty] private string resultHeadline = Strings.Get("Verify_NoResultYet");
        [ObservableProperty] private string resultSubline = "";

        // ===================== Карточка «Общая информация» =====================

        /// <summary>Строки о выбранных файлах: сколько их и какого они размера.</summary>
        [ObservableProperty] private string filesInfoText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowInfoHint))]
        private bool hasInfo;

        /// <summary>Пока не выбрано ни одного файла, показывать нечего — вместо строк подсказка.</summary>
        public bool ShowInfoHint => !HasInfo;

        /// <summary>Как в «Конвертировании»: спрашивать файловую систему на каждую нажатую
        /// клавишу нельзя — на сетевом пути окно подвисало бы на каждую букву.</summary>
        private const int InputSettleDelayMs = 250;

        private int _infoGeneration;

        /// <summary>Последний ответ файловой системы: из него собираются строки
        /// карточки, в том числе заново после смены языка.</summary>
        private string[] _infoPaths = Array.Empty<string>();
        private FileProbeResult[] _infoProbes = Array.Empty<FileProbeResult>();

        public string MaxFilesNote => Strings.Format("Verify_MaxFilesNote", VerifyRequest.MaxFiles);

        /// <summary>
        /// Сравнивать можно, как только выбраны хотя бы два файла: меньше — сравнивать
        /// не с чем. Лишние пустые строки запуску не мешают, их отбрасывает StartAsync —
        /// иначе забытая пустая строка держала бы кнопку серой при двух выбранных файлах.
        /// </summary>
        public bool CanStart =>
            !IsBusy && Files.Count(slot => slot.Path.Length > 0) >= VerifyRequest.MinFiles;

        /// <summary>Пока идёт сравнение, поля вкладки недоступны.</summary>
        public bool IsNotBusy => !IsBusy;

        public bool CanAddFile => !IsBusy && Files.Count < VerifyRequest.MaxFiles;
        public bool CanRemoveFile => !IsBusy && Files.Count > VerifyRequest.MinFiles;

        private CancellationTokenSource? _cts;

        /// <summary>
        /// Оценка «сколько осталось» под полосой прогресса. Часы отдельные, а не
        /// DateTime.Now в каждом отсчёте: разность двух моментов Stopwatch не зависит
        /// от того, перевели ли за это время системные часы.
        /// </summary>
        private readonly SpeedEstimator _speed = new();
        private readonly Stopwatch _clock = new();

        /// <summary>
        /// Итог последнего сравнения — чтобы после смены языка перерисовать карточку
        /// результата, а не сбрасывать её. Все её тексты собираются кодом, поэтому сами
        /// они не переведутся.
        /// </summary>
        private VerifyOutcome? _lastOutcome;

        public VerifyViewModel()
        {
            // Доступность «Сравнить» зависит от путей в строках, а меняются они по-разному:
            // кнопкой «Обзор», вводом с клавиатуры и перетаскиванием. Подписка на сами
            // строки ловит все три случая разом; подписка на коллекцию нужна потому, что
            // строки добавляются и убираются кнопками.
            Files.CollectionChanged += OnFilesChanged;

            for (int i = 0; i < VerifyRequest.MinFiles; i++) Files.Add(new VerifyFileSlot());
            RenumberFiles();
        }

        private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (VerifyFileSlot slot in e.OldItems) slot.PropertyChanged -= OnSlotChanged;

            if (e.NewItems != null)
                foreach (VerifyFileSlot slot in e.NewItems) slot.PropertyChanged += OnSlotChanged;

            NotifyCanStartChanged();

            // Убранная строка могла быть заполненной — в карточке её больше быть не должно.
            ScheduleFilesInfoUpdate();
        }

        private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VerifyFileSlot.Path)) return;

            NotifyCanStartChanged();
            ScheduleFilesInfoUpdate();
        }

        private void NotifyCanStartChanged()
        {
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }

        // ===================== «Общая информация» =====================

        /// <summary>
        /// Пересчёт с задержкой — для набора пути с клавиатуры. Счётчик поколений
        /// отбрасывает устаревшие ответы: пока ждали или ходили на диск, путь мог
        /// смениться ещё раз, и старый размер затёр бы новый. Тот же приём, что
        /// в предпросмотре «Конвертирования».
        /// </summary>
        private async void ScheduleFilesInfoUpdate()
        {
            // Метод возвращает void — ждать его некому, и любое исключение отсюда
            // стало бы необработанным и уронило программу из-за одной строки в карточке.
            try
            {
                int generation = ++_infoGeneration;

                await Task.Delay(InputSettleDelayMs);
                if (generation != _infoGeneration) return;

                string[] paths = Files.Select(f => f.Path).Where(p => p.Length > 0).ToArray();
                var probes = await Task.Run(() => paths.Select(FileProbe.Measure).ToArray());
                if (generation != _infoGeneration) return;

                ApplyFilesInfo(paths, probes);
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_UnexpectedErrorLog",
                    nameof(VerifyViewModel), ex.GetType().Name, ex.Message));
            }
        }

        private void ApplyFilesInfo(string[] paths, FileProbeResult[] probes)
        {
            _infoPaths = paths;
            _infoProbes = probes;
            RenderFilesInfo();
        }

        /// <summary>
        /// Собрать строки из последнего ответа файловой системы. Отдельно от опроса диска,
        /// потому что при смене языка меняются подписи, а не размеры: спрашивать диск
        /// заново незачем, тем более в потоке интерфейса.
        /// </summary>
        private void RenderFilesInfo()
        {
            if (_infoPaths.Length == 0)
            {
                HasInfo = false;
                FilesInfoText = "";
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(Strings.Format("Verify_FilesSelectedLine", _infoPaths.Length));

            for (int i = 0; i < _infoPaths.Length; i++)
            {
                // Имя пустое, если в поле путь к папке или он оборван на разделителе, —
                // тогда показываем то, что введено, а не пустое место.
                string name = Path.GetFileName(_infoPaths[i]);
                if (name.Length == 0) name = _infoPaths[i];

                sb.AppendLine(_infoProbes[i].Exists
                    ? Strings.Format("Verify_InfoFileLine", name, SizeFormatHelper.Format(_infoProbes[i].SizeBytes))
                    : Strings.Format("Verify_InfoFileMissingLine", name));
            }

            FilesInfoText = sb.ToString().TrimEnd();
            HasInfo = true;
        }

        /// <summary>Файл мог смениться на диске, пока смотрели другой раздел.</summary>
        protected override void OnAttached() => ScheduleFilesInfoUpdate();

        protected override void OnLanguageChanged()
        {
            // Подписи «Файл 1»/«Файл 2» и заметка про предел файлов.
            RenumberFiles();
            OnPropertyChanged(nameof(MaxFilesNote));

            // Строки «Общей информации» тоже собраны кодом — пересобираем, иначе
            // размеры остаются подписанными на прежнем языке.
            RenderFilesInfo();

            if (_lastOutcome is { HasResult: true } outcome)
            {
                ShowResultRows(outcome);
                ApplyResultTexts(outcome);
            }
            else
            {
                ResultHeadline = Strings.Get("Verify_NoResultYet");
            }
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanAddFile));
            OnPropertyChanged(nameof(CanRemoveFile));
        }

        // Полоса на кнопке в панели задач показывает тот же общий ход работы, что и полоса
        // на странице, — чтобы за ним не приходилось разворачивать свёрнутое окно. Паузы
        // в сверке нет: она только читает, и обрывать её нечем.
        partial void OnOverallProgressChanged(double value) =>
            OperationLockService.Instance.Progress = value / 100.0;

        /// <summary>
        /// Подписи и доступность кнопок зависят от количества строк, поэтому пересчитываются
        /// после каждого добавления и удаления: иначе после удаления второй из трёх строк
        /// осталась бы нумерация 1 и 3.
        /// </summary>
        private void RenumberFiles()
        {
            for (int i = 0; i < Files.Count; i++)
                Files[i].Label = Strings.Format("Verify_FileLabel", i + 1);

            OnPropertyChanged(nameof(CanAddFile));
            OnPropertyChanged(nameof(CanRemoveFile));
            AddFileCommand.NotifyCanExecuteChanged();
            RemoveFileCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanAddFile))]
        private void AddFile()
        {
            Files.Add(new VerifyFileSlot());
            RenumberFiles();
        }

        [RelayCommand(CanExecute = nameof(CanRemoveFile))]
        private void RemoveFile(VerifyFileSlot? slot)
        {
            if (slot == null || Files.Count <= VerifyRequest.MinFiles) return;

            Files.Remove(slot);
            RenumberFiles();
        }

        [RelayCommand]
        private void BrowseFile(VerifyFileSlot? slot)
        {
            if (slot == null) return;

            var dlg = new OpenFileDialog { Filter = Strings.Get("Common_AllFilesFilter"), Multiselect = true };
            if (dlg.ShowDialog() != true) return;

            // Выбрали сразу несколько — раскладываем по строкам от текущей и вниз,
            // добавляя новые, пока не упрёмся в предел.
            SetPathsFrom(slot, dlg.FileNames);
        }

        /// <summary>
        /// Раскладывает пути начиная с указанной строки. Используется и «Обзором»
        /// с множественным выбором, и перетаскиванием нескольких файлов сразу.
        /// </summary>
        public void SetPathsFrom(VerifyFileSlot slot, IReadOnlyList<string> paths)
        {
            int index = Files.IndexOf(slot);
            if (index < 0 || paths.Count == 0) return;

            foreach (string path in paths)
            {
                if (index >= Files.Count)
                {
                    if (Files.Count >= VerifyRequest.MaxFiles) break;
                    Files.Add(new VerifyFileSlot());
                }

                Files[index].Path = path;
                index++;
            }

            RenumberFiles();
            ScheduleFilesInfoUpdate();
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            // Пустые строки в сравнение не идут: их можно добавить кнопкой и не заполнить,
            // а операция считает пустой путь ненайденным файлом и отвечает окном ошибки.
            var paths = Files.Select(f => f.Path).Where(path => path.Length > 0).ToArray();
            var request = new VerifyRequest { FilePaths = paths };

            _cts = new CancellationTokenSource();

            var progress = new Progress<VerifyProgress>(p =>
            {
                OverallProgress = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 0;

                _speed.Add(_clock.Elapsed, p.TotalBytesProcessed);
                CurrentFileLabel = ProgressCaption.Build(
                    Strings.Format("Verify_FileLabelProgress", p.FileIndex, p.FileCount, p.FileName),
                    _speed.BytesPerSecond, _speed.Remaining(p.TotalBytes));
            });

            try
            {
                var outcome = await VerifyOperation.RunAsync(
                    request, progress, AppLogger.Log, _cts.Token, MarkOperationStarted);

                _lastOutcome = outcome;
                ShowResultRows(outcome);

                // Расхождение хэшей — тоже красная полоса, а не только ошибка чтения:
                // ради этого ответа сверку и запускают, и «файлы разные» — ровно тот
                // случай, когда возвращаться к окну надо не дожидаясь конца.
                OperationLockService.Instance.ReportResult(
                    outcome.Status is not (VerifyStatus.Failed or VerifyStatus.Different));

                await ShowOutcomeAsync(outcome);
            }
            finally
            {
                OverallProgress = 0;
                CurrentFileLabel = "";
                IsBusy = false;
                OperationLockService.Instance.OperationFinished();
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>Вызывается операцией, когда файлы найдены и сравнение началось.</summary>
        private void MarkOperationStarted()
        {
            IsBusy = true;
            // Как в Конвертировании и Сборке: пока считаем хэши, переключение разделов
            // в меню заблокировано — иначе можно уйти со вкладки и потерять процесс из виду.
            OperationLockService.Instance.OperationStarted(CancelNow);
            HasResult = false;
            ResultHeadline = Strings.Get("Verify_NoResultYet");
            ResultSubline = "";
            ResultRows.Clear();
            _lastOutcome = null;
            OverallProgress = 0;

            // Часы пускаются здесь, а не при нажатии «Начать»: до первого прочитанного
            // байта успевает пройти проверка, что все файлы на месте.
            _speed.Reset();
            _clock.Restart();
        }

        /// <summary>
        /// Строки карточки результата.
        ///
        /// При трёх и более файлах это группы совпавших хэшей, а не попарные сравнения:
        /// пять файлов дали бы десять пар, по которым не видно, какой файл выбивается,
        /// а по группам видно сразу.
        ///
        /// При двух файлах группы только мешают: получалось две группы по одному файлу
        /// с номерами, которые больше нигде в программе не встречаются, — а сказать надо
        /// ровно одно, «вот хэш первого, вот хэш второго». Поэтому здесь строка на файл
        /// и те же подписи, что у полей ввода: если имена файлов совпадают, номер строки —
        /// единственное, чем их различить.
        /// </summary>
        private void ShowResultRows(VerifyOutcome outcome)
        {
            ResultRows.Clear();

            // Отменённое и упавшее сравнение показывать нечем: хэши посчитаны не для всех,
            // а половина ответа хуже, чем его отсутствие.
            if (!outcome.HasResult) return;

            if (outcome.FilePaths.Count == VerifyRequest.MinFiles && outcome.Hashes.Count == VerifyRequest.MinFiles)
            {
                for (int i = 0; i < outcome.FilePaths.Count; i++)
                {
                    ResultRows.Add(new VerifyResultRow
                    {
                        Title = Strings.Format("Verify_FileLabel", i + 1),
                        Hash = outcome.Hashes[i],
                        FileNames = Path.GetFileName(outcome.FilePaths[i])
                    });
                }
                return;
            }

            bool single = outcome.Groups.Count == 1;
            for (int i = 0; i < outcome.Groups.Count; i++)
            {
                var group = outcome.Groups[i];
                ResultRows.Add(new VerifyResultRow
                {
                    Title = single
                        ? Strings.Get("Verify_SingleGroupTitle")
                        : Strings.Format("Verify_GroupTitle", i + 1, group.FileCount, PluralForms.Files(group.FileCount)),
                    Hash = group.Hash,
                    FileNames = string.Join("\n", group.FilePaths.Select(Path.GetFileName))
                });
            }
        }

        /// <summary>
        /// Заголовок и пояснение в карточке результата. Отдельным методом, потому что
        /// вызывается дважды: по окончании сравнения и заново при смене языка.
        /// </summary>
        private void ApplyResultTexts(VerifyOutcome outcome)
        {
            bool allSame = outcome.Status == VerifyStatus.AllIdentical;

            IsResultGood = allSame;
            ResultHeadline = Strings.Get(allSame ? "Verify_AllIdenticalHeadline" : "Verify_DifferenceHeadline");

            // Форма слова «файл» зависит от числа: 2 файла, но 5 файлов (см. PluralForms).
            string files = PluralForms.Files(outcome.FileCount);

            if (allSame)
            {
                ResultSubline = Strings.Format("Verify_AllIdenticalSubline", outcome.FileCount, files);
            }
            else if (outcome.LargestGroupSize > 1)
            {
                // Когда совпавших пар нет вовсе, «1 из 5 идентичны» звучало бы странно.
                ResultSubline = Strings.Format("Verify_DifferenceSubline", outcome.LargestGroupSize, outcome.FileCount);
            }
            else
            {
                // При двух файлах «Все 2 файла различаются» звучит как пересчёт по головам:
                // «всех» там ровно столько, сколько файлов вообще может быть. С трёх и
                // больше «Все» уже к месту.
                ResultSubline = outcome.FileCount > 2
                    ? Strings.Format("Verify_AllDifferentSubline", outcome.FileCount, files)
                    : Strings.Format("Verify_BothDifferentSubline", outcome.FileCount, files);
            }

            HasResult = true;
        }

        private async Task ShowOutcomeAsync(VerifyOutcome outcome)
        {
            switch (outcome.Status)
            {
                case VerifyStatus.NotEnoughFiles:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"), Strings.Get("Verify_SelectBothFiles"));
                    break;

                case VerifyStatus.FileMissing:
                    await DialogService.ShowWarningAsync(Strings.Get("Common_CannotStartTitle"),
                        Strings.Format("Verify_FilesMissingMessage",
                            string.Join("\n", outcome.MissingFilePaths.Select(p => string.IsNullOrWhiteSpace(p) ? "—" : p))));
                    break;

                case VerifyStatus.AllIdentical:
                    ApplyResultTexts(outcome);
                    // Итог сообщается окном, как в Конвертировании и Сборке: операция может
                    // идти долго, и человек к этому моменту уже мог отойти от компьютера.
                    await DialogService.ShowInfoWithHashesAsync(
                        Strings.Get("Common_DoneTitle"), BuildResultMessage(), BuildHashRows());
                    break;

                case VerifyStatus.Different:
                    ApplyResultTexts(outcome);
                    // Заголовок тот же, что при совпадении: сравнение прошло как надо,
                    // просто файлы разные. «Ошибка проверки» здесь читалась так, будто
                    // не сработала сама проверка, — а её итог как раз и есть ответ.
                    // Что файлы разошлись, видно по красной карточке результата.
                    await DialogService.ShowInfoWithHashesAsync(
                        Strings.Get("Common_DoneTitle"), BuildResultMessage(), BuildHashRows());
                    break;

                case VerifyStatus.Cancelled:
                    await DialogService.ShowInfoAsync(Strings.Get("Common_CancelledTitle"), Strings.Get("Verify_CancelledMessage"));
                    break;

                case VerifyStatus.Failed:
                    await DialogService.ShowErrorAsync(Strings.Get("Common_Error"),
                        Strings.Format("Verify_HashErrorMessage", outcome.ErrorMessage));
                    break;
            }
        }

        /// <summary>
        /// Текст окна с итогом — заголовок исхода и пояснение под ним. То же, что
        /// в верхней части карточки результата, поэтому окно и карточка не могут
        /// разойтись.
        /// </summary>
        private string BuildResultMessage() =>
            ResultSubline.Length > 0 ? ResultHeadline + "\n\n" + ResultSubline : ResultHeadline;

        /// <summary>
        /// Хэши для окна — отдельными строками, а не внутри текста: рядом с каждой
        /// стоит кнопка копирования. Хэш из сверки нужен не «на посмотреть» — его
        /// вписывают в «Ожидаемый SHA-256» при сборке и им же ищут, чем отличаются
        /// файлы, — а 64 знака из текста окна оставалось только перепечатывать.
        /// Строки те же и в том же порядке, что в карточке результата.
        /// </summary>
        private DialogService.HashRow[] BuildHashRows() => ResultRows
            .Select(row => new DialogService.HashRow(row.Title, row.Hash, row.FileNames))
            .ToArray();

        /// <summary>
        /// Спрашивает, прежде чем прервать. Сообщение здесь своё: сравнение только читает
        /// файлы, удалять после него нечего, и пугать человека потерянными файлами
        /// было бы неправдой. Esc на странице ведёт сюда же.
        /// </summary>
        [RelayCommand(CanExecute = nameof(IsBusy))]
        private async Task CancelAsync()
        {
            var answer = await DialogService.ShowConfirmAsync(
                Strings.Get("Common_CancelConfirmTitle"),
                Strings.Get("Common_CancelConfirmReadingMessage"),
                Strings.Get("Common_CancelConfirmStop"),
                null,
                Strings.Get("Common_CancelConfirmKeep"));

            if (answer == DialogChoice.Primary) CancelNow();
        }

        /// <summary>Прервать молча — этим же пользуется закрытие окна.</summary>
        private void CancelNow() => _cts?.Cancel();
    }
}
