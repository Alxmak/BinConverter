using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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

    /// <summary>Одна группа одинаковых хэшей в карточке результата.</summary>
    public sealed class VerifyGroupView
    {
        public string Title { get; init; } = "";
        public string Hash { get; init; } = "";

        /// <summary>Имена файлов этой группы, по одному в строке.</summary>
        public string FileNames { get; init; } = "";
    }

    public partial class VerifyViewModel : LogHostViewModel
    {
        /// <summary>Строки выбора файлов. Их число меняется кнопками «Добавить»/«Убрать».</summary>
        public ObservableCollection<VerifyFileSlot> Files { get; } = new();

        /// <summary>Группы одинаковых хэшей — то, что показывается в карточке результата.</summary>
        public ObservableCollection<VerifyGroupView> ResultGroups { get; } = new();

        [ObservableProperty] private double overallProgress;
        [ObservableProperty] private string currentFileLabel = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(AddFileCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveFileCommand))]
        private bool isBusy;

        [ObservableProperty] private bool hasResult;

        /// <summary>Все файлы совпали — от этого зависит цвет баннера результата.</summary>
        [ObservableProperty] private bool isResultGood;

        [ObservableProperty] private string resultHeadline = Strings.Get("Verify_NoResultYet");
        [ObservableProperty] private string resultSubline = "";

        public string MaxFilesNote => Strings.Format("Verify_MaxFilesNote", VerifyRequest.MaxFiles);

        /// <summary>
        /// Сравнивать можно, когда заполнены все строки: пустая строка — это не выбранный
        /// файл, и до правки нажатие на «Сравнить» с такой строкой заканчивалось окном
        /// «файл не найден» с пустым путём в списке.
        /// </summary>
        public bool CanStart => !IsBusy && Files.All(slot => slot.Path.Length > 0);

        /// <summary>Пока идёт сравнение, поля вкладки недоступны.</summary>
        public bool IsNotBusy => !IsBusy;

        public bool CanAddFile => !IsBusy && Files.Count < VerifyRequest.MaxFiles;
        public bool CanRemoveFile => !IsBusy && Files.Count > VerifyRequest.MinFiles;

        private CancellationTokenSource? _cts;

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
        }

        private void OnSlotChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VerifyFileSlot.Path)) NotifyCanStartChanged();
        }

        private void NotifyCanStartChanged()
        {
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }

        protected override void OnLanguageChanged()
        {
            // Подписи «Файл 1»/«Файл 2» и заметка про предел файлов.
            RenumberFiles();
            OnPropertyChanged(nameof(MaxFilesNote));

            if (_lastOutcome is { HasResult: true } outcome)
            {
                ShowGroups(outcome);
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
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            var paths = Files.Select(f => f.Path).ToArray();
            var request = new VerifyRequest { FilePaths = paths };

            _cts = new CancellationTokenSource();

            var progress = new Progress<VerifyProgress>(p =>
            {
                OverallProgress = p.TotalBytes > 0 ? (double)p.TotalBytesProcessed / p.TotalBytes * 100.0 : 0;
                CurrentFileLabel = Strings.Format("Verify_FileLabelProgress", p.FileIndex, p.FileCount, p.FileName);
            });

            try
            {
                var outcome = await VerifyOperation.RunAsync(
                    request, progress, AppLogger.Log, _cts.Token, MarkOperationStarted);

                _lastOutcome = outcome;
                ShowGroups(outcome);
                await ShowOutcomeAsync(outcome);
            }
            finally
            {
                OverallProgress = 0;
                CurrentFileLabel = "";
                IsBusy = false;
                OperationLockService.Instance.IsBusy = false;
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
            OperationLockService.Instance.IsBusy = true;
            HasResult = false;
            ResultHeadline = Strings.Get("Verify_NoResultYet");
            ResultSubline = "";
            ResultGroups.Clear();
            _lastOutcome = null;
            OverallProgress = 0;
        }

        /// <summary>
        /// Показываем не попарные сравнения, а группы совпавших хэшей: по ним сразу видно,
        /// сколько файлов идентичны и какой именно выбивается.
        /// </summary>
        private void ShowGroups(VerifyOutcome outcome)
        {
            ResultGroups.Clear();

            bool single = outcome.Groups.Count == 1;
            for (int i = 0; i < outcome.Groups.Count; i++)
            {
                var group = outcome.Groups[i];
                ResultGroups.Add(new VerifyGroupView
                {
                    Title = single
                        ? Strings.Get("Verify_SingleGroupTitle")
                        : Strings.Format("Verify_GroupTitle", i + 1, group.FileCount),
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

            if (allSame)
            {
                ResultSubline = Strings.Format("Verify_AllIdenticalSubline", outcome.FileCount);
            }
            else
            {
                // Когда совпавших пар нет вовсе, «1 из 5 идентичны» звучало бы странно.
                ResultSubline = outcome.LargestGroupSize > 1
                    ? Strings.Format("Verify_DifferenceSubline", outcome.LargestGroupSize, outcome.FileCount)
                    : Strings.Format("Verify_AllDifferentSubline", outcome.FileCount);
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
                    await DialogService.ShowInfoAsync(Strings.Get("Common_DoneTitle"), BuildResultMessage());
                    break;

                case VerifyStatus.Different:
                    ApplyResultTexts(outcome);
                    await DialogService.ShowErrorAsync(Strings.Get("Common_VerifyErrorTitle"), BuildResultMessage());
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
        /// Текст окна с итогом. Собирается из того же, что показано в карточке результата,
        /// — заголовок исхода, пояснение и группы совпавших хэшей, — поэтому окно и карточка
        /// не могут разойтись. Хэши переносим по строкам: в окно 64 знака одной строкой
        /// не влезают (см. HashDisplay).
        /// </summary>
        private string BuildResultMessage()
        {
            var sb = new StringBuilder();
            sb.Append(ResultHeadline);
            if (ResultSubline.Length > 0) sb.Append("\n\n").Append(ResultSubline);

            foreach (var group in ResultGroups)
            {
                sb.Append("\n\n").Append(group.Title).Append('\n').Append(HashDisplay.Wrap(group.Hash));
                if (group.FileNames.Length > 0) sb.Append('\n').Append(group.FileNames);
            }

            return sb.ToString();
        }

        [RelayCommand(CanExecute = nameof(IsBusy))]
        private void Cancel() => _cts?.Cancel();
    }
}
