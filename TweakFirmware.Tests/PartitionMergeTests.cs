using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using TweakFirmware.Core.Operations;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Обратная сборка дампа из извлечённых разделов.
    ///
    /// Всё, что нужно для сборки, лежит в именах файлов, и потому главная проверка здесь —
    /// что образ собирается побайтово тем же, каким его резали. Остальное про то, как
    /// сборка отказывается работать: файл не того размера, два куска на одних байтах,
    /// файл результата среди собираемых. Каждый такой случай молча испортил бы прошивку,
    /// и заметить это можно было бы только по хэшу — если было с чем сверять.
    /// </summary>
    public class PartitionMergeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tfw-pmerge-" + Guid.NewGuid().ToString("N"));
        private readonly string _parts;
        private readonly List<string> _log = new();

        public PartitionMergeTests()
        {
            _parts = Path.Combine(_root, "parts");
            Directory.CreateDirectory(_parts);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
        }

        // ============ Имена файлов ============

        [Fact]
        public void ParsedName_GivesBackWhatWeWroteIntoIt()
        {
            string name = PartitionFileNaming.ForPartition(0x30C0000, 0x2BC00000, "system", FsType.Ext4);

            Assert.True(PartitionFileNaming.TryParse(name, out var info));
            Assert.Equal(0x30C0000, info.Offset);
            Assert.Equal(0x2BC00000, info.Length);

            // Расширение файловой системы дописали мы сами — в имени раздела его не было.
            Assert.Equal("system", info.Name);
        }

        [Theory]
        [InlineData("analysis.json")]
        [InlineData("partitions_2026-01-01.udev")]
        [InlineData("boot.bin")]
        [InlineData("USER_0x0000000000_boot.bin")]          // нет размера
        [InlineData("USER_0xZZ_0x00000100_boot.bin")]       // не шестнадцатеричный адрес
        [InlineData("USER_0x0000000000_0x00000000_boot.bin")] // нулевая длина
        [InlineData("USER_0x0000000000_0x00000100_.bin")]   // нет имени
        public void NamesThatAreNotPartitions_AreRejected(string name)
        {
            Assert.False(PartitionFileNaming.TryParse(name, out _));
        }

        // ============ План сборки ============

        [Fact]
        public void Plan_SortsPiecesByAddressAndFindsTheGapBetweenThem()
        {
            WritePiece(0x200, 0x100, "rootfs", 0xBB);
            WritePiece(0x000, 0x100, "boot", 0xAA);

            var plan = PartitionMergePlan.Build(_parts);

            Assert.Equal(new[] { "boot", "rootfs" }, plan.Pieces.Select(p => p.Name));
            Assert.Equal(0x300, plan.TotalSize);
            Assert.Equal(0x200, plan.DataSize);
            Assert.Equal(0x100, plan.GapSize);

            var gap = Assert.Single(plan.Gaps);
            Assert.Equal(0x100, gap.Offset);
            Assert.Equal(0x100, gap.Length);

            Assert.True(plan.CanMerge);
        }

        [Fact]
        public void Plan_RefusesAFileWhoseSizeDoesNotMatchItsName()
        {
            // Имя обещает 0x100 байт, а в файле их вдвое меньше: положить такой кусок
            // в образ значит сдвинуть всё, что идёт после него.
            WriteNamed(PartitionFileNaming.ForPartition(0, 0x100, "boot", FsType.Unknown), 0x80, 0xAA);

            var plan = PartitionMergePlan.Build(_parts);

            Assert.Empty(plan.Pieces);
            Assert.False(plan.CanMerge);

            var issue = Assert.Single(plan.Issues);
            Assert.Equal(MergeIssueKind.SizeMismatch, issue.Kind);
            Assert.Equal(0x100, issue.Expected);
            Assert.Equal(0x80, issue.Actual);
        }

        [Fact]
        public void Plan_RefusesPiecesThatClaimTheSameBytes()
        {
            WritePiece(0x000, 0x200, "boot", 0xAA);
            WritePiece(0x100, 0x100, "rootfs", 0xBB);

            var plan = PartitionMergePlan.Build(_parts);

            Assert.False(plan.CanMerge);

            var issue = Assert.Single(plan.Issues);
            Assert.Equal(MergeIssueKind.Overlap, issue.Kind);
            Assert.Equal(0x100, issue.Offset);
        }

        [Fact]
        public void Plan_CountsFilesThatAreNotPartitions()
        {
            WritePiece(0, 0x100, "boot", 0xAA);
            File.WriteAllText(Path.Combine(_parts, "analysis.json"), "{}");
            File.WriteAllText(Path.Combine(_parts, "partitions.udev"), "[DESC]");

            var plan = PartitionMergePlan.Build(_parts);

            Assert.Single(plan.Pieces);
            Assert.Equal(2, plan.SkippedFiles.Count);

            // Посторонние файлы сборке не мешают: рядом с разделами лежат и отчёт,
            // и .udev, и это нормальная папка извлечения.
            Assert.True(plan.CanMerge);
        }

        [Fact]
        public void Plan_OfAFolderWithoutPartitions_CannotBeMerged()
        {
            var plan = PartitionMergePlan.Build(_parts);

            Assert.False(plan.CanMerge);
            Assert.Equal(0, plan.TotalSize);
        }

        // ============ Сборка ============

        [Fact]
        public async Task Merge_PutsEachPieceAtItsAddressAndFillsTheGap()
        {
            WritePiece(0x000, 0x100, "boot", 0xAA);
            WritePiece(0x200, 0x100, "rootfs", 0xBB);

            string output = Path.Combine(_root, "merged.bin");
            var result = await PartitionMerger.MergeAsync(
                PartitionMergePlan.Build(_parts), output, 0xFF, null, Log, CancellationToken.None);

            byte[] image = File.ReadAllBytes(output);

            Assert.Equal(0x300, image.Length);
            Assert.All(image[..0x100], b => Assert.Equal(0xAA, b));
            Assert.All(image[0x100..0x200], b => Assert.Equal(0xFF, b));
            Assert.All(image[0x200..], b => Assert.Equal(0xBB, b));

            // Хэш считается по тому, что записано, включая заполнение, — то есть
            // относится к готовому файлу и сверяется с хэшем исходного дампа.
            Assert.Equal(HashOf(image), result.ResultHash);
        }

        [Fact]
        public async Task Merge_FillsTheGapWithZerosWhenAsked()
        {
            WritePiece(0x000, 0x100, "boot", 0xAA);
            WritePiece(0x200, 0x100, "rootfs", 0xBB);

            string output = Path.Combine(_root, "merged.bin");
            await PartitionMerger.MergeAsync(
                PartitionMergePlan.Build(_parts), output, 0x00, null, Log, CancellationToken.None);

            byte[] image = File.ReadAllBytes(output);
            Assert.All(image[0x100..0x200], b => Assert.Equal(0x00, b));
        }

        /// <summary>
        /// Главное, ради чего всё это: папка, покрывающая дамп целиком, собирается обратно
        /// побайтово в тот же дамп. Извлечение как раз такую и делает — промежутки между
        /// разделами попадают в таблицу отдельными записями.
        /// </summary>
        [Fact]
        public async Task Merge_OfAFullFolder_IsByteForByteTheOriginalDump()
        {
            byte[] dump = new byte[0x1000];
            new Random(1234).NextBytes(dump);

            var layout = new (long Offset, int Length, string Name)[]
            {
                (0x000, 0x300, "boot"),
                (0x300, 0x100, "gap_01"),
                (0x400, 0xC00, "rootfs")
            };

            foreach (var (offset, length, name) in layout)
            {
                File.WriteAllBytes(
                    Path.Combine(_parts, PartitionFileNaming.ForPartition(offset, length, name, FsType.Unknown)),
                    dump.AsSpan((int)offset, length).ToArray());
            }

            string output = Path.Combine(_root, "merged.bin");
            var result = await PartitionMerger.MergeAsync(
                PartitionMergePlan.Build(_parts), output, 0xFF, null, Log, CancellationToken.None);

            Assert.Equal(dump, File.ReadAllBytes(output));
            Assert.Equal(HashOf(dump), result.ResultHash);
        }

        // ============ Операция ============

        [Fact]
        public async Task FolderWithoutPartitions_Reported()
        {
            var outcome = await RunAsync(new PartitionMergeRequest
            {
                SourceFolder = _parts,
                OutputPath = Path.Combine(_root, "merged.bin")
            });

            Assert.Equal(PartitionMergeStatus.NothingToMerge, outcome.Status);
            Assert.Empty(_log);
        }

        [Fact]
        public async Task MissingFolder_Reported()
        {
            var outcome = await RunAsync(new PartitionMergeRequest
            {
                SourceFolder = Path.Combine(_root, "нет-такой"),
                OutputPath = Path.Combine(_root, "merged.bin")
            });

            Assert.Equal(PartitionMergeStatus.SourceFolderNotFound, outcome.Status);
        }

        [Fact]
        public async Task BrokenLayout_StopsBeforeTheFirstByte()
        {
            WritePiece(0x000, 0x200, "boot", 0xAA);
            WritePiece(0x100, 0x100, "rootfs", 0xBB);

            string output = Path.Combine(_root, "merged.bin");
            var outcome = await RunAsync(new PartitionMergeRequest { SourceFolder = _parts, OutputPath = output });

            Assert.Equal(PartitionMergeStatus.LayoutBroken, outcome.Status);
            Assert.NotEmpty(outcome.Plan.Issues);
            Assert.False(File.Exists(output));
        }

        [Fact]
        public async Task OutputAmongThePieces_Reported()
        {
            string piece = WritePiece(0, 0x100, "boot", 0xAA);

            var outcome = await RunAsync(new PartitionMergeRequest { SourceFolder = _parts, OutputPath = piece });

            // Файл результата создаётся до чтения кусков: собирать «в самого себя»
            // значит потерять раздел безвозвратно.
            Assert.Equal(PartitionMergeStatus.OutputInsideSource, outcome.Status);
            Assert.Equal(0x100, new FileInfo(piece).Length);
        }

        [Fact]
        public async Task NotEnoughSpace_StopsBeforeTheFirstByte()
        {
            WritePiece(0, 0x100, "boot", 0xAA);

            string output = Path.Combine(_root, "merged.bin");
            var outcome = await PartitionMergeOperation.RunAsync(
                new PartitionMergeRequest { SourceFolder = _parts, OutputPath = output, CheckDiskSpace = true },
                new AlwaysOverwriteResolver(), null, Log, null, CancellationToken.None, null,
                spaceCheck: (_, needed) => new SpaceCheckResult { RequiredBytes = needed, AvailableBytes = 0 });

            Assert.Equal(PartitionMergeStatus.NotEnoughSpace, outcome.Status);
            Assert.False(File.Exists(output));
        }

        [Fact]
        public async Task Cancelled_RemovesTheIncompleteFile()
        {
            WritePiece(0, 0x100, "boot", 0xAA);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            string output = Path.Combine(_root, "merged.bin");
            var outcome = await PartitionMergeOperation.RunAsync(
                new PartitionMergeRequest { SourceFolder = _parts, OutputPath = output },
                new AlwaysOverwriteResolver(), null, Log, null, cts.Token);

            Assert.Equal(PartitionMergeStatus.Cancelled, outcome.Status);
            Assert.False(File.Exists(output));
        }

        [Fact]
        public async Task Merge_ReportsHashAndSizeOfWhatItWrote()
        {
            WritePiece(0x000, 0x100, "boot", 0xAA);
            WritePiece(0x100, 0x100, "rootfs", 0xBB);

            string output = Path.Combine(_root, "merged.bin");
            var outcome = await RunAsync(new PartitionMergeRequest { SourceFolder = _parts, OutputPath = output });

            Assert.True(outcome.Succeeded);
            Assert.Equal(2, outcome.PiecesUsed);
            Assert.Equal(0x200, outcome.TotalBytes);
            Assert.Equal(HashOf(File.ReadAllBytes(output)), outcome.ResultHash);
        }

        // ============ Поток вызывающего ============

        /// <summary>
        /// Сборка обязана оставаться в потоке того, кто её позвал: вопрос о перезаписи
        /// строит окно, а сигнал «работа началась» будит команды вкладки — и то, и другое
        /// в чужом потоке падает с «вызывающий поток не может получить доступ к данному
        /// объекту». Один ConfigureAwait(false) после осмотра папки уже уводил туда всё,
        /// что идёт следом, и вкладка после этого оставалась с невыключенной занятостью:
        /// кнопка «Начать» выглядела живой и не отвечала на нажатие.
        ///
        /// Окна в тестах нет, поэтому поток интерфейса изображает свой контекст
        /// синхронизации: продолжения он складывает в очередь, а разбирает её только
        /// поток теста. Так вопрос «остались ли мы у себя» сводится к сравнению номеров.
        /// </summary>
        [Fact]
        public void Merge_StaysOnTheCallersThread()
        {
            WritePiece(0x000, 0x100, "boot", 0xAA);
            WritePiece(0x100, 0x100, "rootfs", 0xBB);

            // Файл результата уже есть — иначе про перезапись никто не спросит.
            string output = Path.Combine(_root, "merged.bin");
            File.WriteAllBytes(output, new byte[] { 0x00 });

            int callerThread = Environment.CurrentManagedThreadId;
            int askedOn = 0;
            int startedOn = 0;

            var pump = new PumpingContext();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(pump);

            PartitionMergeOutcome outcome;
            try
            {
                var task = PartitionMergeOperation.RunAsync(
                    new PartitionMergeRequest { SourceFolder = _parts, OutputPath = output },
                    new ThreadRecordingResolver(id => askedOn = id), null, Log, null, CancellationToken.None,
                    onStarted: () => startedOn = Environment.CurrentManagedThreadId);

                outcome = pump.RunUntilCompleted(task);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Assert.True(outcome.Succeeded);
            Assert.Equal(callerThread, askedOn);
            Assert.Equal(callerThread, startedOn);
        }

        // ============ Вспомогательное ============

        private Task<PartitionMergeOutcome> RunAsync(PartitionMergeRequest request) =>
            PartitionMergeOperation.RunAsync(
                request, new AlwaysOverwriteResolver(), null, Log, null, CancellationToken.None);

        private void Log(string line) => _log.Add(line);

        private string WritePiece(long offset, int length, string name, byte value) =>
            WriteNamed(PartitionFileNaming.ForPartition(offset, length, name, FsType.Unknown), length, value);

        private string WriteNamed(string fileName, int length, byte value)
        {
            string path = Path.Combine(_parts, fileName);

            byte[] data = new byte[length];
            Array.Fill(data, value);
            File.WriteAllBytes(path, data);

            return path;
        }

        private static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        private sealed class AlwaysOverwriteResolver : IConflictResolver
        {
            public Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName) =>
                Task.FromResult(ConflictDecision.Overwrite);

            public Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath) =>
                Task.FromResult(ConflictDecision.Overwrite);
        }

        /// <summary>
        /// Соглашается на перезапись и запоминает, в каком потоке его об этом спросили.
        /// Отвечает готовым результатом, без собственного ожидания: своё ожидание вернуло
        /// бы работу в контекст теста само по себе — и проверка перестала бы что-либо
        /// значить.
        /// </summary>
        private sealed class ThreadRecordingResolver : IConflictResolver
        {
            private readonly Action<int> _record;

            public ThreadRecordingResolver(Action<int> record) => _record = record;

            public Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName) =>
                Task.FromResult(ConflictDecision.Overwrite);

            public Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath)
            {
                _record(Environment.CurrentManagedThreadId);
                return Task.FromResult(ConflictDecision.Overwrite);
            }
        }

        /// <summary>
        /// Заменитель потока интерфейса. Продолжения не выполняет сразу, а складывает
        /// в очередь; разбирает её только тот поток, который позвал
        /// <c>RunUntilCompleted</c>. Всё, что ушло мимо этой очереди, окажется
        /// в потоке пула — и проверка это увидит.
        /// </summary>
        private sealed class PumpingContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = new();

            public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

            /// <summary>
            /// Крутит очередь, пока работа не кончится, и отдаёт её итог. Ограничение
            /// по времени — чтобы сорвавшаяся операция не подвесила весь прогон тестов
            /// молча. Ожидание живёт здесь, а не в самом тесте: блокирующее ожидание
            /// в теле теста запрещено анализатором xUnit, а прогон идёт с
            /// «предупреждение = ошибка».
            /// </summary>
            public T RunUntilCompleted<T>(Task<T> task)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);

                while (!task.IsCompleted)
                {
                    if (_queue.TryTake(out var item, millisecondsTimeout: 50)) item.Work(item.State);
                    else if (DateTime.UtcNow > deadline) throw new TimeoutException("Сборка не завершилась за 30 секунд.");
                }

                // Хвост очереди: последние продолжения могли встать в неё уже после того,
                // как задача отчиталась о завершении.
                while (_queue.TryTake(out var rest)) rest.Work(rest.State);

                return task.GetAwaiter().GetResult();
            }
        }
    }
}
