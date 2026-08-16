using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Operations;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Сравнение файлов по SHA-256. Смысл раздела — не попарные сравнения, а группировка:
    /// пять файлов дали бы десять пар, по которым не видно, какой именно выбивается.
    /// Поэтому группировка проверяется отдельно и подробно.
    ///
    /// Здесь же сведение прогресса всех файлов в одну шкалу: раньше этот расчёт лежал
    /// во ViewModel и был непроверяем, а ошибка в нём давала бар, который доходит
    /// до половины и замирает.
    /// </summary>
    public class VerifyOperationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tfw-verify-op-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _log = new();

        public VerifyOperationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ============ Группировка (чистая логика, без диска) ============

        [Fact]
        public void GroupByHash_AllSame_OneGroupWithEveryFile()
        {
            var groups = VerifyOperation.GroupByHash(
                new[] { "a", "b", "c" },
                new[] { "AA", "AA", "AA" });

            Assert.Single(groups);
            Assert.Equal(3, groups[0].FileCount);
            Assert.Equal(new[] { "a", "b", "c" }, groups[0].FilePaths);
        }

        [Fact]
        public void GroupByHash_AllDifferent_GroupPerFile()
        {
            var groups = VerifyOperation.GroupByHash(
                new[] { "a", "b", "c" },
                new[] { "AA", "BB", "CC" });

            Assert.Equal(3, groups.Count);
            Assert.All(groups, g => Assert.Equal(1, g.FileCount));
        }

        [Fact]
        public void GroupByHash_LargestGroupComesFirst()
        {
            // Один файл выбивается — именно этот случай пользователь и ищет.
            var groups = VerifyOperation.GroupByHash(
                new[] { "плохой", "a", "b", "c", "d" },
                new[] { "BAD", "OK", "OK", "OK", "OK" });

            Assert.Equal(2, groups.Count);
            Assert.Equal(4, groups[0].FileCount);
            Assert.Equal("OK", groups[0].Hash);
            Assert.Equal(1, groups[1].FileCount);
            // По второй группе видно, какой файл не такой, как остальные.
            Assert.Equal(new[] { "плохой" }, groups[1].FilePaths);
        }

        [Fact]
        public void GroupByHash_EqualSizes_KeepOrderOfFirstAppearance()
        {
            var groups = VerifyOperation.GroupByHash(
                new[] { "a", "b", "c", "d" },
                new[] { "YY", "ZZ", "YY", "ZZ" });

            Assert.Equal(2, groups.Count);
            Assert.Equal("YY", groups[0].Hash);
            Assert.Equal("ZZ", groups[1].Hash);
        }

        [Fact]
        public void GroupByHash_CaseIsIgnored()
        {
            // Хэши приходят из одного места и регистр у них один, но сравнение хэшей
            // по регистру — классический источник ложных расхождений.
            var groups = VerifyOperation.GroupByHash(
                new[] { "a", "b" },
                new[] { "abc123", "ABC123" });

            Assert.Single(groups);
            Assert.Equal(2, groups[0].FileCount);
        }

        [Fact]
        public void GroupByHash_UncomputedHashesAreSkipped()
        {
            // Пустая строка — файл, который не успели посчитать при отмене.
            var groups = VerifyOperation.GroupByHash(
                new[] { "a", "b", "c" },
                new[] { "AA", "", "AA" });

            Assert.Single(groups);
            Assert.Equal(new[] { "a", "c" }, groups[0].FilePaths);
        }

        [Fact]
        public void GroupByHash_NothingComputed_NoGroups()
        {
            Assert.Empty(VerifyOperation.GroupByHash(new[] { "a", "b" }, new[] { "", "" }));
        }

        // ============ Сколько файлов ============

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task FewerThanTwoFiles_Reported(int count)
        {
            var paths = Enumerable.Range(0, count).Select(i => MakeFile($"f{i}.bin", 1024, seed: i)).ToArray();

            var outcome = await RunAsync(paths);

            Assert.Equal(VerifyStatus.NotEnoughFiles, outcome.Status);
            Assert.False(outcome.HasResult);
            Assert.Empty(_log);
        }

        [Fact]
        public async Task TwoIdenticalFiles_AllIdentical()
        {
            string a = MakeFile("a.bin", 4096, seed: 1);
            string b = CopyOf(a, "b.bin");

            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.AllIdentical, outcome.Status);
            Assert.Single(outcome.Groups);
            Assert.Equal(2, outcome.FileCount);
            Assert.Equal(2, outcome.LargestGroupSize);
        }

        [Fact]
        public async Task FiveIdenticalFiles_AllIdentical()
        {
            string a = MakeFile("a.bin", 2048, seed: 1);
            var paths = new[] { a, CopyOf(a, "b.bin"), CopyOf(a, "c.bin"), CopyOf(a, "d.bin"), CopyOf(a, "e.bin") };

            var outcome = await RunAsync(paths);

            Assert.Equal(VerifyStatus.AllIdentical, outcome.Status);
            Assert.Single(outcome.Groups);
            Assert.Equal(5, outcome.FileCount);
            Assert.Equal(5, outcome.LargestGroupSize);
            Assert.Equal(5, outcome.Groups[0].FileCount);
        }

        [Fact]
        public async Task FourOfFiveIdentical_DifferentAndTheOddFileIsNamed()
        {
            string a = MakeFile("a.bin", 2048, seed: 1);
            string odd = MakeFile("odd.bin", 2048, seed: 99);
            var paths = new[] { a, CopyOf(a, "b.bin"), odd, CopyOf(a, "c.bin"), CopyOf(a, "d.bin") };

            var outcome = await RunAsync(paths);

            Assert.Equal(VerifyStatus.Different, outcome.Status);
            Assert.True(outcome.HasResult);
            Assert.Equal(2, outcome.Groups.Count);
            Assert.Equal(4, outcome.LargestGroupSize);
            // Главное для пользователя: понять, какой файл не такой.
            Assert.Equal(new[] { odd }, outcome.Groups[1].FilePaths);
        }

        [Fact]
        public async Task DifferentContentSameSize_Different()
        {
            // Одинаковый размер — единственный случай, где без хэша разницу не увидеть.
            string a = MakeFile("a.bin", 4096, seed: 1);
            string b = MakeFile("b.bin", 4096, seed: 2);

            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.Different, outcome.Status);
            Assert.Equal(2, outcome.Groups.Count);
        }

        [Fact]
        public async Task SingleByteDifference_IsCaught()
        {
            string a = MakeFile("a.bin", 4096, seed: 1);
            string b = CopyOf(a, "b.bin");
            var bytes = File.ReadAllBytes(b);
            bytes[2048] ^= 0xFF;
            File.WriteAllBytes(b, bytes);

            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.Different, outcome.Status);
        }

        [Fact]
        public async Task EmptyFiles_AllIdentical()
        {
            // Крайний случай: делить на общий размер нельзя, но упасть операция не должна.
            var outcome = await RunAsync(MakeFile("a.bin", 0, seed: 1), MakeFile("b.bin", 0, seed: 2));

            Assert.Equal(VerifyStatus.AllIdentical, outcome.Status);
        }

        [Fact]
        public async Task HashesAreReportedPerFileInInputOrder()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            string b = MakeFile("b.bin", 1024, seed: 2);
            string c = CopyOf(a, "c.bin");

            var outcome = await RunAsync(a, b, c);

            Assert.Equal(3, outcome.Hashes.Count);
            Assert.Equal(outcome.Hashes[0], outcome.Hashes[2]);
            Assert.NotEqual(outcome.Hashes[0], outcome.Hashes[1]);
        }

        // ============ Отсутствующие файлы ============

        [Fact]
        public async Task MissingFile_ReportedWithItsPathAndNothingIsRead()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            string missing = Path.Combine(_root, "нет-такого.bin");

            var outcome = await RunAsync(a, missing);

            Assert.Equal(VerifyStatus.FileMissing, outcome.Status);
            Assert.Equal(new[] { missing }, outcome.MissingFilePaths);
            Assert.Empty(_log);
        }

        [Fact]
        public async Task SeveralMissingFiles_AllOfThemReported()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            string m1 = Path.Combine(_root, "нет1.bin");
            string m2 = Path.Combine(_root, "нет2.bin");

            var outcome = await RunAsync(a, m1, m2);

            Assert.Equal(VerifyStatus.FileMissing, outcome.Status);
            Assert.Equal(2, outcome.MissingFilePaths.Count);
        }

        [Fact]
        public async Task BlankPathCountsAsMissing()
        {
            var outcome = await RunAsync(MakeFile("a.bin", 1024, seed: 1), "   ");

            Assert.Equal(VerifyStatus.FileMissing, outcome.Status);
        }

        [Fact]
        public async Task MissingFile_DoesNotSignalStart()
        {
            int started = 0;

            await VerifyOperation.RunAsync(
                new VerifyRequest { FilePaths = new[] { Path.Combine(_root, "нет.bin"), Path.Combine(_root, "тоже-нет.bin") } },
                null, Log, CancellationToken.None, onStarted: () => started++);

            Assert.Equal(0, started);
        }

        [Fact]
        public async Task EveryFilePresent_SignalsStartOnce()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            int started = 0;

            await VerifyOperation.RunAsync(
                new VerifyRequest { FilePaths = new[] { a, CopyOf(a, "b.bin") } },
                null, Log, CancellationToken.None, onStarted: () => started++);

            Assert.Equal(1, started);
        }

        // ============ Общая шкала прогресса ============

        [Fact]
        public async Task Progress_CoversEveryFileOnOneScale()
        {
            string a = MakeFile("a.bin", 3 * 1024 * 1024, seed: 1);
            string b = MakeFile("b.bin", 2 * 1024 * 1024, seed: 1);
            string c = MakeFile("c.bin", 1024 * 1024, seed: 1);
            var reports = new List<VerifyProgress>();

            await RunAsync(new SyncProgress<VerifyProgress>(reports.Add), a, b, c);

            Assert.NotEmpty(reports);

            long total = 6 * 1024 * 1024;
            Assert.All(reports, r => Assert.Equal(total, r.TotalBytes));
            Assert.All(reports, r => Assert.Equal(3, r.FileCount));

            for (int i = 1; i < reports.Count; i++)
                Assert.True(reports[i].TotalBytesProcessed >= reports[i - 1].TotalBytesProcessed,
                    "прогресс не должен идти назад при переходе с одного файла на другой");

            Assert.Equal(total, reports[^1].TotalBytesProcessed);
        }

        [Fact]
        public async Task Progress_EachFileContinuesWhereThePreviousStopped()
        {
            string a = MakeFile("a.bin", 2 * 1024 * 1024, seed: 1);
            string b = MakeFile("b.bin", 2 * 1024 * 1024, seed: 1);
            string c = MakeFile("c.bin", 2 * 1024 * 1024, seed: 1);
            var reports = new List<VerifyProgress>();

            await RunAsync(new SyncProgress<VerifyProgress>(reports.Add), a, b, c);

            // Иначе бар прыгал бы назад на каждом переходе к следующему файлу.
            Assert.All(reports.Where(r => r.FileIndex == 2), r => Assert.True(r.TotalBytesProcessed >= 2 * 1024 * 1024));
            Assert.All(reports.Where(r => r.FileIndex == 3), r => Assert.True(r.TotalBytesProcessed >= 4 * 1024 * 1024));
        }

        [Fact]
        public async Task Progress_NamesAndNumbersTheFileBeingRead()
        {
            string a = MakeFile("первый.bin", 2 * 1024 * 1024, seed: 1);
            string b = MakeFile("второй.bin", 2 * 1024 * 1024, seed: 1);
            var reports = new List<VerifyProgress>();

            await RunAsync(new SyncProgress<VerifyProgress>(reports.Add), a, b);

            Assert.Contains(reports, r => r.FileIndex == 1 && r.FileName == "первый.bin");
            Assert.Contains(reports, r => r.FileIndex == 2 && r.FileName == "второй.bin");
        }

        // ============ Отмена ============

        [Fact]
        public async Task Cancelled_KeepsWhateverWasAlreadyComputed()
        {
            string a = MakeFile("a.bin", 4 * 1024 * 1024, seed: 1);
            string b = CopyOf(a, "b.bin");
            using var cts = new CancellationTokenSource();

            // Отменяем, как только начали читать второй файл: хэш первого уже готов.
            var progress = new SyncProgress<VerifyProgress>(p =>
            {
                if (p.FileIndex == 2) cts.Cancel();
            });

            var outcome = await VerifyOperation.RunAsync(
                new VerifyRequest { FilePaths = new[] { a, b } }, progress, Log, cts.Token);

            Assert.Equal(VerifyStatus.Cancelled, outcome.Status);
            Assert.False(outcome.HasResult);
            Assert.NotEqual("", outcome.Hashes[0]);
            Assert.Equal("", outcome.Hashes[1]);
        }

        [Fact]
        public async Task CancelledImmediately_NoHashesAtAll()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await VerifyOperation.RunAsync(
                new VerifyRequest { FilePaths = new[] { a, CopyOf(a, "b.bin") } }, null, Log, cts.Token);

            Assert.Equal(VerifyStatus.Cancelled, outcome.Status);
            Assert.All(outcome.Hashes, h => Assert.Equal("", h));
        }

        // ============ Границы ============

        [Fact]
        public void FileLimits_AreTwoToFive()
        {
            // На эти числа опирается интерфейс: кнопка «Добавить файл» и надпись о максимуме.
            Assert.Equal(2, VerifyRequest.MinFiles);
            Assert.Equal(5, VerifyRequest.MaxFiles);
        }

        // ============ Вспомогательное ============

        // ============ Пути рядом с хэшами ============

        [Fact]
        public async Task Outcome_KeepsFilePathsPairedWithHashes()
        {
            // Интерфейс при двух файлах показывает не группы, а строку на файл, и берёт
            // пару «путь — хэш» отсюда: поля ввода к тому моменту уже могли переписать.
            string a = MakeFile("a.bin", 1024, seed: 1);
            string b = MakeFile("b.bin", 1024, seed: 2);

            var outcome = await RunAsync(a, b);

            Assert.Equal(new[] { a, b }, outcome.FilePaths);
            Assert.Equal(outcome.FilePaths.Count, outcome.Hashes.Count);
            Assert.All(outcome.Hashes, hash => Assert.NotEmpty(hash));
        }

        [Fact]
        public async Task Outcome_KeepsFilePaths_EvenWhenAFileIsMissing()
        {
            string a = MakeFile("a.bin", 512, seed: 1);
            string missing = Path.Combine(_root, "нет-такого.bin");

            var outcome = await RunAsync(a, missing);

            Assert.Equal(VerifyStatus.FileMissing, outcome.Status);
            Assert.Equal(new[] { a, missing }, outcome.FilePaths);
        }

        private string MakeFile(string name, int sizeBytes, int seed)
        {
            string path = Path.Combine(_root, name);
            var bytes = new byte[sizeBytes];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string CopyOf(string source, string name)
        {
            string path = Path.Combine(_root, name);
            File.Copy(source, path);
            return path;
        }

        private void Log(string line) => _log.Add(line);

        private Task<VerifyOutcome> RunAsync(params string[] paths) =>
            VerifyOperation.RunAsync(new VerifyRequest { FilePaths = paths }, null, Log, CancellationToken.None);

        private Task<VerifyOutcome> RunAsync(IProgress<VerifyProgress> progress, params string[] paths) =>
            VerifyOperation.RunAsync(new VerifyRequest { FilePaths = paths }, progress, Log, CancellationToken.None);
    }
}
