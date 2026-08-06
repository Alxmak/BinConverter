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
    /// Сравнение двух файлов по SHA-256. Кроме самого вердикта здесь проверяется сведение
    /// прогресса двух файлов в одну шкалу: раньше этот расчёт лежал во ViewModel и был
    /// непроверяем, а ошибка в нём давала бар, который доходит до половины и замирает.
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

        // ============ Вердикт ============

        [Fact]
        public async Task IdenticalFiles_Match()
        {
            string a = MakeFile("a.bin", 4096, seed: 1);
            string b = CopyOf(a, "b.bin");

            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.Match, outcome.Status);
            Assert.True(outcome.HasResult);
            Assert.Equal(outcome.HashA, outcome.HashB);
        }

        [Fact]
        public async Task DifferentContent_SameSize_Mismatch()
        {
            // Одинаковый размер — единственный случай, где без хэша разницу не увидеть.
            string a = MakeFile("a.bin", 4096, seed: 1);
            string b = MakeFile("b.bin", 4096, seed: 2);

            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.Mismatch, outcome.Status);
            Assert.True(outcome.HasResult);
            Assert.NotEqual(outcome.HashA, outcome.HashB);
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

            Assert.Equal(VerifyStatus.Mismatch, outcome.Status);
        }

        [Fact]
        public async Task SameFileComparedWithItself_Match()
        {
            string a = MakeFile("a.bin", 2048, seed: 3);

            var outcome = await RunAsync(a, a);

            Assert.Equal(VerifyStatus.Match, outcome.Status);
        }

        [Fact]
        public async Task EmptyFiles_Match()
        {
            string a = MakeFile("a.bin", 0, seed: 1);
            string b = MakeFile("b.bin", 0, seed: 2);

            // Крайний случай: делить на общий размер нельзя, но упасть операция не должна.
            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.Match, outcome.Status);
        }

        // ============ Отсутствующие файлы ============

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task MissingFile_ReportedWithoutReadingAnything(bool aExists, bool bExists)
        {
            string a = aExists ? MakeFile("a.bin", 1024, seed: 1) : Path.Combine(_root, "нет-a.bin");
            string b = bExists ? MakeFile("b.bin", 1024, seed: 1) : Path.Combine(_root, "нет-b.bin");

            var outcome = await RunAsync(a, b);

            Assert.Equal(VerifyStatus.FileMissing, outcome.Status);
            Assert.False(outcome.HasResult);
            Assert.Empty(_log);
        }

        [Fact]
        public async Task MissingFile_DoesNotSignalStart()
        {
            int started = 0;

            await VerifyOperation.RunAsync(
                new VerifyRequest { FileAPath = Path.Combine(_root, "нет.bin"), FileBPath = Path.Combine(_root, "тоже-нет.bin") },
                null, Log, CancellationToken.None, onStarted: () => started++);

            Assert.Equal(0, started);
        }

        [Fact]
        public async Task BothFilesPresent_SignalsStartOnce()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            string b = CopyOf(a, "b.bin");
            int started = 0;

            await VerifyOperation.RunAsync(
                new VerifyRequest { FileAPath = a, FileBPath = b },
                null, Log, CancellationToken.None, onStarted: () => started++);

            Assert.Equal(1, started);
        }

        // ============ Общая шкала прогресса ============

        [Fact]
        public async Task Progress_CoversBothFilesOnOneScale()
        {
            string a = MakeFile("a.bin", 3 * 1024 * 1024, seed: 1);
            string b = MakeFile("b.bin", 2 * 1024 * 1024, seed: 1);
            var reports = new List<VerifyProgress>();

            await VerifyOperation.RunAsync(
                new VerifyRequest { FileAPath = a, FileBPath = b },
                new SyncProgress<VerifyProgress>(reports.Add), Log, CancellationToken.None);

            Assert.NotEmpty(reports);

            // Общий размер один и тот же во всех отчётах — это и есть «одна шкала».
            long total = 5 * 1024 * 1024;
            Assert.All(reports, r => Assert.Equal(total, r.TotalBytes));

            // Прогресс не убывает и доходит до конца.
            for (int i = 1; i < reports.Count; i++)
                Assert.True(reports[i].TotalBytesProcessed >= reports[i - 1].TotalBytesProcessed,
                    "прогресс не должен идти назад при переходе со первого файла на второй");

            Assert.Equal(total, reports[^1].TotalBytesProcessed);
            Assert.True(reports[^1].TotalBytesProcessed <= reports[^1].TotalBytes, "прогресс не должен превышать 100%");
        }

        [Fact]
        public async Task Progress_SecondFileContinuesAfterTheFirst()
        {
            string a = MakeFile("a.bin", 2 * 1024 * 1024, seed: 1);
            string b = MakeFile("b.bin", 2 * 1024 * 1024, seed: 1);
            var reports = new List<VerifyProgress>();

            await VerifyOperation.RunAsync(
                new VerifyRequest { FileAPath = a, FileBPath = b },
                new SyncProgress<VerifyProgress>(reports.Add), Log, CancellationToken.None);

            var first = reports.Where(r => r.FileIndex == 1).ToList();
            var second = reports.Where(r => r.FileIndex == 2).ToList();

            Assert.NotEmpty(first);
            Assert.NotEmpty(second);

            // Отчёты второго файла обязаны начинаться там, где кончились отчёты первого,
            // а не с нуля: иначе бар прыгал бы назад на середине сравнения.
            Assert.True(second[0].TotalBytesProcessed >= first[^1].TotalBytesProcessed);
            Assert.All(second, r => Assert.True(r.TotalBytesProcessed >= 2 * 1024 * 1024));
        }

        [Fact]
        public async Task Progress_NamesTheFileBeingRead()
        {
            string a = MakeFile("первый.bin", 2 * 1024 * 1024, seed: 1);
            string b = MakeFile("второй.bin", 2 * 1024 * 1024, seed: 1);
            var reports = new List<VerifyProgress>();

            await VerifyOperation.RunAsync(
                new VerifyRequest { FileAPath = a, FileBPath = b },
                new SyncProgress<VerifyProgress>(reports.Add), Log, CancellationToken.None);

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
                new VerifyRequest { FileAPath = a, FileBPath = b }, progress, Log, cts.Token);

            Assert.Equal(VerifyStatus.Cancelled, outcome.Status);
            Assert.False(outcome.HasResult);
            // Посчитанное не должно пропадать с экрана.
            Assert.NotEqual("", outcome.HashA);
            Assert.Equal("", outcome.HashB);
        }

        [Fact]
        public async Task CancelledImmediately_NoHashesAtAll()
        {
            string a = MakeFile("a.bin", 1024, seed: 1);
            string b = CopyOf(a, "b.bin");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await VerifyOperation.RunAsync(
                new VerifyRequest { FileAPath = a, FileBPath = b }, null, Log, cts.Token);

            Assert.Equal(VerifyStatus.Cancelled, outcome.Status);
            Assert.Equal("", outcome.HashA);
            Assert.Equal("", outcome.HashB);
        }

        // ============ Вспомогательное ============

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

        private Task<VerifyOutcome> RunAsync(string a, string b) =>
            VerifyOperation.RunAsync(new VerifyRequest { FileAPath = a, FileBPath = b }, null, Log, CancellationToken.None);
    }
}
