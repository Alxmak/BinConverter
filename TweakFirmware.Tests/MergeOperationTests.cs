using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using TweakFirmware.Core.Operations;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Обвязка вокруг сборки цепочки: проверки, перезапись существующего файла, расчёт
    /// размера цепочки, сверка с ожидаемым хэшем, уборка за срывом. Сама склейка байтов —
    /// в FileSplitterMergerTests.
    /// </summary>
    public class MergeOperationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tfw-merge-op-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _log = new();

        public MergeOperationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ============ Проверки до начала работы ============

        [Fact]
        public async Task SourceMissing_Reported()
        {
            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = Path.Combine(_root, "нет-такого.bin"),
                OutputPath = Path.Combine(_root, "merged.bin")
            });

            Assert.Equal(MergeStatus.SourceNotFound, outcome.Status);
            Assert.Empty(_log);
        }

        [Fact]
        public async Task OutputPathEmpty_Reported()
        {
            var chain = await MakeChain(4 * 1024, partSize: 1024);

            var outcome = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = "  " });

            Assert.Equal(MergeStatus.OutputPathNotSpecified, outcome.Status);
        }

        [Fact]
        public async Task ChainWithGap_MergesShorterFileWithoutComplaining_WhichIsWhyExpectedHashExists()
        {
            string basePath = await MakeChain(4 * 1024, partSize: 1024);
            string output = Path.Combine(_root, "merged.bin");

            // Выламываем цепочку: остаётся только базовый файл, продолжения нет.
            foreach (string part in Directory.GetFiles(Path.GetDirectoryName(basePath)!))
                if (part != basePath) File.Delete(part);

            var outcome = await RunAsync(new MergeRequest { AnyChainFilePath = basePath, OutputPath = output });

            // Поиск цепочки останавливается на разрыве, поэтому собирается ТОЛЬКО то, что нашлось:
            // ошибки нет, а файл получается короче настоящего. Ровно от этого и защищает
            // поле «Ожидаемый SHA-256» — само по себе неполноту сборка не замечает.
            Assert.Equal(MergeStatus.Completed, outcome.Status);
            Assert.Equal(1, outcome.PartsUsed);
            Assert.Equal(1024, new FileInfo(output).Length);
        }

        [Fact]
        public async Task ChainWithGap_ExpectedHashCatchesTheIncompleteResult()
        {
            // Тот же разрыв, но с известным хэшем полной сборки — расхождение обязано вскрыться.
            string basePath = await MakeChain(4 * 1024, partSize: 1024);
            var full = await RunAsync(new MergeRequest { AnyChainFilePath = basePath, OutputPath = Path.Combine(_root, "full.bin") });

            foreach (string part in Directory.GetFiles(Path.GetDirectoryName(basePath)!))
                if (part != basePath) File.Delete(part);

            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = basePath,
                OutputPath = Path.Combine(_root, "broken.bin"),
                ExpectedHash = full.MergedHash
            });

            Assert.Equal(MergeStatus.HashMismatch, outcome.Status);
            Assert.False(outcome.Succeeded);
        }

        // ============ Размер цепочки ============

        [Fact]
        public void GetChainSize_SumsEveryPart()
        {
            string basePath = MakeChain(6 * 1024, partSize: 1024).GetAwaiter().GetResult();

            long size = MergeOperation.GetChainSize(basePath, out var chain);

            Assert.Equal(6 * 1024, size);
            Assert.Equal(6, chain.Count);
        }

        [Fact]
        public void GetChainSize_WorksFromAnyPartNotJustTheBase()
        {
            string basePath = MakeChain(4 * 1024, partSize: 1024).GetAwaiter().GetResult();
            var parts = Directory.GetFiles(Path.GetDirectoryName(basePath)!);
            Array.Sort(parts);

            // Цепочка должна находиться по любому своему файлу — так работает и перетаскивание.
            long fromLast = MergeOperation.GetChainSize(parts[^1], out _);

            Assert.Equal(4 * 1024, fromLast);
        }

        // ============ Существующий файл результата ============

        [Fact]
        public async Task ExistingOutput_Cancel_LeavesItUntouched()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);
            string output = Path.Combine(_root, "merged.bin");
            File.WriteAllText(output, "старое содержимое");

            var outcome = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = output },
                ConflictDecision.Cancel);

            Assert.Equal(MergeStatus.CancelledBeforeStart, outcome.Status);
            Assert.Equal("старое содержимое", File.ReadAllText(output));
            Assert.Empty(_log);
        }

        [Fact]
        public async Task ExistingOutput_UseAlternative_KeepsBothFiles()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);
            string output = Path.Combine(_root, "merged.bin");
            File.WriteAllText(output, "старое содержимое");

            var outcome = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = output },
                ConflictDecision.UseAlternative);

            Assert.True(outcome.Succeeded);
            Assert.True(outcome.OutputPathChanged);
            Assert.NotEqual(output, outcome.OutputPath);
            Assert.Equal("старое содержимое", File.ReadAllText(output));
            Assert.Equal(4 * 1024, new FileInfo(outcome.OutputPath).Length);
        }

        [Fact]
        public async Task ExistingOutput_Overwrite_ReplacesIt()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);
            string output = Path.Combine(_root, "merged.bin");
            File.WriteAllText(output, "старое содержимое");

            var outcome = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = output },
                ConflictDecision.Overwrite);

            Assert.True(outcome.Succeeded);
            Assert.False(outcome.OutputPathChanged);
            Assert.Equal(4 * 1024, new FileInfo(output).Length);
        }

        [Fact]
        public async Task FreshOutput_ResolverNeverAsked()
        {
            string chain = await MakeChain(2 * 1024, partSize: 1024);
            var resolver = new CountingResolver();

            var outcome = await MergeOperation.RunAsync(
                new MergeRequest { AnyChainFilePath = chain, OutputPath = Path.Combine(_root, "новый.bin") },
                resolver, null, Log, null, CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal(0, resolver.Calls);
        }

        // ============ Ожидаемый хэш ============

        [Fact]
        public async Task NoExpectedHash_StatusIsPlainCompleted()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);

            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = chain,
                OutputPath = Path.Combine(_root, "merged.bin"),
                ExpectedHash = ""
            });

            Assert.Equal(MergeStatus.Completed, outcome.Status);
            Assert.NotEqual("", outcome.MergedHash);
        }

        [Fact]
        public async Task ExpectedHashMatches_StatusIsHashMatch()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);
            string firstPass = Path.Combine(_root, "first.bin");
            var first = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = firstPass });

            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = chain,
                OutputPath = Path.Combine(_root, "second.bin"),
                ExpectedHash = first.MergedHash
            });

            Assert.Equal(MergeStatus.HashMatch, outcome.Status);
            Assert.True(outcome.Succeeded);
        }

        [Theory]
        [InlineData("  {0}  ")]   // лишние пробелы вокруг
        [InlineData("{0}")]
        public async Task ExpectedHash_IsTrimmedBeforeComparing(string template)
        {
            string chain = await MakeChain(2 * 1024, partSize: 1024);
            var first = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = Path.Combine(_root, "a.bin") });

            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = chain,
                OutputPath = Path.Combine(_root, "b.bin"),
                ExpectedHash = string.Format(template, first.MergedHash)
            });

            Assert.Equal(MergeStatus.HashMatch, outcome.Status);
        }

        [Fact]
        public async Task ExpectedHash_ComparedIgnoringCase()
        {
            string chain = await MakeChain(2 * 1024, partSize: 1024);
            var first = await RunAsync(new MergeRequest { AnyChainFilePath = chain, OutputPath = Path.Combine(_root, "a.bin") });

            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = chain,
                OutputPath = Path.Combine(_root, "b.bin"),
                ExpectedHash = first.MergedHash.ToUpperInvariant()
            });

            Assert.Equal(MergeStatus.HashMatch, outcome.Status);
        }

        [Fact]
        public async Task ExpectedHashWrong_StatusIsMismatchButFileIsStillThere()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);
            string output = Path.Combine(_root, "merged.bin");

            var outcome = await RunAsync(new MergeRequest
            {
                AnyChainFilePath = chain,
                OutputPath = output,
                ExpectedHash = new string('0', 64)
            });

            Assert.Equal(MergeStatus.HashMismatch, outcome.Status);
            Assert.False(outcome.Succeeded);
            // Файл не удаляем: расхождение хэша — повод предупредить, а не уничтожать результат.
            Assert.True(File.Exists(output));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void HasExpectedHash_FalseForBlank(string? given)
        {
            var request = new MergeRequest { ExpectedHash = given ?? "" };
            Assert.False(request.HasExpectedHash);
        }

        // ============ Нехватка места ============

        [Fact]
        public async Task NotEnoughSpace_NothingIsWrittenAndOperationNeverStarts()
        {
            string chain = await MakeChain(4 * 1024, partSize: 1024);
            string output = Path.Combine(_root, "merged.bin");
            int started = 0;

            var outcome = await MergeOperation.RunAsync(
                new MergeRequest { AnyChainFilePath = chain, OutputPath = output },
                new FixedConflictPolicy(ConflictDecision.Overwrite), null, Log, null, CancellationToken.None,
                onStarted: () => started++,
                spaceCheck: (_, needed) => new SpaceCheckResult { RequiredBytes = needed, AvailableBytes = needed / 2 });

            Assert.Equal(MergeStatus.NotEnoughSpace, outcome.Status);
            Assert.Equal(0, started);
            Assert.False(File.Exists(output));
            // Размер цепочки нужен диалогу — он должен быть посчитан до отказа.
            Assert.Equal(4 * 1024, outcome.ChainSizeBytes);
        }

        // ============ Отмена ============

        [Fact]
        public async Task Cancelled_RemovesThePartiallyWrittenResult()
        {
            string chain = await MakeChain(6 * 1024 * 1024, partSize: 512 * 1024);
            string output = Path.Combine(_root, "merged.bin");
            using var cts = new CancellationTokenSource();

            var progress = new SyncProgress<MergeProgress>(_ => cts.Cancel());

            var outcome = await MergeOperation.RunAsync(
                new MergeRequest { AnyChainFilePath = chain, OutputPath = output },
                new FixedConflictPolicy(ConflictDecision.Overwrite), progress, Log, null, cts.Token);

            Assert.Equal(MergeStatus.Cancelled, outcome.Status);
            // Недописанный собранный файл опаснее всего: он выглядит готовой прошивкой.
            Assert.False(File.Exists(output));
        }

        // ============ Вспомогательное ============

        /// <summary>Создаёт настоящую цепочку частей и возвращает путь к её базовому файлу.</summary>
        private async Task<string> MakeChain(int totalBytes, long partSize)
        {
            string source = Path.Combine(_root, "chain-source.bin");
            var bytes = new byte[totalBytes];
            new Random(7).NextBytes(bytes);
            File.WriteAllBytes(source, bytes);

            string folder = Path.Combine(_root, "chain-" + Guid.NewGuid().ToString("N")[..8]);
            await FileSplitter.SplitAsync(source, folder, "emmc.bin", partSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            File.Delete(source);
            return Path.Combine(folder, "emmc.bin");
        }

        private void Log(string line) => _log.Add(line);

        private Task<MergeOutcome> RunAsync(MergeRequest request, ConflictDecision onConflict = ConflictDecision.Overwrite) =>
            MergeOperation.RunAsync(request, new FixedConflictPolicy(onConflict), null, Log, null, CancellationToken.None);

        private sealed class CountingResolver : IConflictResolver
        {
            public int Calls { get; private set; }

            public Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName)
            {
                Calls++;
                return Task.FromResult(ConflictDecision.Cancel);
            }

            public Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath)
            {
                Calls++;
                return Task.FromResult(ConflictDecision.Cancel);
            }
        }
    }
}
