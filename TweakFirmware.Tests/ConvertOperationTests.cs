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
    /// Нарезка файла на части — самая опасная операция программы: она пишет пользователю
    /// многогигабайтные файлы и удаляет их при срыве. До выноса из ConvertViewModel эта
    /// логика была наглухо приварена к WPF и не покрывалась ничем.
    ///
    /// Проверяется не сама нарезка (это FileSplitterMergerTests), а обвязка вокруг неё:
    /// порядок проверок, разрешение конфликта имён, уборка за сбоем.
    /// </summary>
    public class ConvertOperationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tfw-convert-op-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _log = new();

        public ConvertOperationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ============ Проверки до начала работы ============

        [Fact]
        public async Task SourceMissing_ReportedWithoutTouchingDisk()
        {
            string outFolder = Path.Combine(_root, "out");

            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = Path.Combine(_root, "нет-такого.bin"),
                OutputFolder = outFolder,
                MaxPartSizeBytes = 1024
            });

            Assert.Equal(ConvertStatus.SourceNotFound, outcome.Status);
            // Папку назначения создавать не должны: операция не начиналась.
            Assert.False(Directory.Exists(outFolder));
            Assert.Empty(_log);
        }

        [Fact]
        public async Task OutputFolderEmpty_Reported()
        {
            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(1024),
                OutputFolder = "   ",
                MaxPartSizeBytes = 1024
            });

            Assert.Equal(ConvertStatus.OutputFolderNotSpecified, outcome.Status);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-4096)]
        public async Task NonPositiveLimit_Reported(long limit)
        {
            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(1024),
                OutputFolder = Path.Combine(_root, "out"),
                MaxPartSizeBytes = limit
            });

            // Иначе это ушло бы в FileSplitter и вылетело исключением вместо внятного статуса.
            Assert.Equal(ConvertStatus.InvalidPartSize, outcome.Status);
        }

        [Fact]
        public async Task ChecksRunInOrder_MissingSourceWinsOverEmptyFolderAndBadLimit()
        {
            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = Path.Combine(_root, "нет-такого.bin"),
                OutputFolder = "",
                MaxPartSizeBytes = 0
            });

            Assert.Equal(ConvertStatus.SourceNotFound, outcome.Status);
        }

        // ============ Имя базового файла ============

        [Theory]
        [InlineData("", "emmc.bin")]
        [InlineData("   ", "emmc.bin")]
        [InlineData("  dump.bin  ", "dump.bin")]
        [InlineData("dump.bin", "dump.bin")]
        public void EffectiveBaseFileName_FallsBackAndTrims(string given, string expected)
        {
            var request = new ConvertRequest { BaseFileName = given };
            Assert.Equal(expected, request.EffectiveBaseFileName);
        }

        [Fact]
        public async Task EmptyBaseFileName_ProducesDefaultNameOnDisk()
        {
            string outFolder = Path.Combine(_root, "out");

            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(2048),
                OutputFolder = outFolder,
                BaseFileName = "",
                MaxPartSizeBytes = 4096
            });

            Assert.True(outcome.Succeeded);
            Assert.True(File.Exists(Path.Combine(outFolder, ConvertRequest.DefaultBaseFileName)));
        }

        // ============ Конфликт имён ============

        [Fact]
        public async Task Conflict_Cancel_WritesNothing()
        {
            string outFolder = Path.Combine(_root, "out");
            Directory.CreateDirectory(outFolder);
            File.WriteAllText(Path.Combine(outFolder, "emmc.bin"), "старое содержимое");

            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(2048),
                OutputFolder = outFolder,
                BaseFileName = "emmc.bin",
                MaxPartSizeBytes = 4096
            }, ConflictDecision.Cancel);

            Assert.Equal(ConvertStatus.CancelledBeforeStart, outcome.Status);
            // Главное: существующий файл остался как был.
            Assert.Equal("старое содержимое", File.ReadAllText(Path.Combine(outFolder, "emmc.bin")));
            Assert.Empty(_log);
        }

        [Fact]
        public async Task Conflict_UseAlternative_WritesToNeighbourFolderAndKeepsTheOldOne()
        {
            string outFolder = Path.Combine(_root, "out");
            Directory.CreateDirectory(outFolder);
            string existing = Path.Combine(outFolder, "emmc.bin");
            File.WriteAllText(existing, "старое содержимое");

            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(2048),
                OutputFolder = outFolder,
                BaseFileName = "emmc.bin",
                MaxPartSizeBytes = 4096
            }, ConflictDecision.UseAlternative);

            Assert.True(outcome.Succeeded);
            Assert.True(outcome.OutputFolderChanged);
            Assert.NotEqual(outFolder, outcome.OutputFolder);
            Assert.Equal("старое содержимое", File.ReadAllText(existing));
            Assert.True(File.Exists(Path.Combine(outcome.OutputFolder, "emmc.bin")));
        }

        [Fact]
        public async Task Conflict_Overwrite_ReplacesTheFileInPlace()
        {
            string outFolder = Path.Combine(_root, "out");
            Directory.CreateDirectory(outFolder);
            string existing = Path.Combine(outFolder, "emmc.bin");
            File.WriteAllText(existing, "старое содержимое");

            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(2048),
                OutputFolder = outFolder,
                BaseFileName = "emmc.bin",
                MaxPartSizeBytes = 4096
            }, ConflictDecision.Overwrite);

            Assert.True(outcome.Succeeded);
            Assert.False(outcome.OutputFolderChanged);
            Assert.Equal(outFolder, outcome.OutputFolder);
            Assert.Equal(2048, new FileInfo(existing).Length);
        }

        [Fact]
        public async Task NoConflict_ResolverNeverAsked()
        {
            var resolver = new CountingResolver(ConflictDecision.Cancel);

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(1024),
                    OutputFolder = Path.Combine(_root, "пустая"),
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 4096
                },
                resolver, null, null, Log, null, CancellationToken.None);

            Assert.True(outcome.Succeeded);
            Assert.Equal(0, resolver.Calls);
        }

        // ============ Начало работы ============

        [Fact]
        public async Task OnStarted_NotCalledWhenChecksFail()
        {
            int started = 0;

            await ConvertOperation.RunAsync(
                new ConvertRequest { SourcePath = Path.Combine(_root, "нет.bin"), OutputFolder = _root, MaxPartSizeBytes = 1024 },
                new FixedConflictPolicy(ConflictDecision.Cancel), null, null, Log, null, CancellationToken.None,
                onStarted: () => started++);

            // Интерфейс не должен гасить поля, а очередь — считать задание запущенным.
            Assert.Equal(0, started);
        }

        [Fact]
        public async Task OnStarted_CalledOnceBeforeWork()
        {
            int started = 0;

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(4096),
                    OutputFolder = Path.Combine(_root, "out"),
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 1024
                },
                new FixedConflictPolicy(ConflictDecision.Overwrite), null, null, Log, null, CancellationToken.None,
                onStarted: () => started++);

            Assert.True(outcome.Succeeded);
            Assert.Equal(1, started);
        }

        [Fact]
        public async Task OnStarted_NotCalledWhenUserRefusesOverwrite()
        {
            string outFolder = Path.Combine(_root, "out");
            Directory.CreateDirectory(outFolder);
            File.WriteAllText(Path.Combine(outFolder, "emmc.bin"), "х");
            int started = 0;

            await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(1024),
                    OutputFolder = outFolder,
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 1024
                },
                new FixedConflictPolicy(ConflictDecision.Cancel), null, null, Log, null, CancellationToken.None,
                onStarted: () => started++);

            Assert.Equal(0, started);
        }

        // ============ Проверка хэша ============

        [Fact]
        public async Task VerifyHashOff_StatusIsPlainCompleted()
        {
            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(4096),
                OutputFolder = Path.Combine(_root, "out"),
                BaseFileName = "emmc.bin",
                MaxPartSizeBytes = 1024,
                VerifyHash = false
            });

            Assert.Equal(ConvertStatus.Completed, outcome.Status);
            Assert.Equal("", outcome.SourceHash);
        }

        [Fact]
        public async Task VerifyHashOn_StatusIsVerifiedAndHashesReported()
        {
            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(4096),
                OutputFolder = Path.Combine(_root, "out"),
                BaseFileName = "emmc.bin",
                MaxPartSizeBytes = 1024,
                VerifyHash = true
            });

            Assert.Equal(ConvertStatus.CompletedVerified, outcome.Status);
            Assert.NotEqual("", outcome.SourceHash);
            Assert.Equal(outcome.SourceHash, outcome.RecombinedHash);
            Assert.True(outcome.Succeeded);
        }

        [Fact]
        public async Task PartsCreatedMatchesFilesOnDisk()
        {
            string outFolder = Path.Combine(_root, "out");

            var outcome = await RunAsync(new ConvertRequest
            {
                SourcePath = MakeSource(4096),
                OutputFolder = outFolder,
                BaseFileName = "emmc.bin",
                MaxPartSizeBytes = 1024
            });

            Assert.Equal(4, outcome.PartsCreated);
            Assert.Equal(4, Directory.GetFiles(outFolder).Length);
            Assert.Equal(4096, outcome.TotalBytes);
        }

        // ============ Нехватка места ============

        [Fact]
        public async Task NotEnoughSpace_NothingIsWrittenAndOperationNeverStarts()
        {
            string outFolder = Path.Combine(_root, "out");
            int started = 0;

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(4096),
                    OutputFolder = outFolder,
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 1024,
                    CheckDiskSpace = true
                },
                new FixedConflictPolicy(ConflictDecision.Overwrite), null, null, Log, null, CancellationToken.None,
                onStarted: () => started++,
                spaceCheck: (_, needed) => new SpaceCheckResult { RequiredBytes = needed, AvailableBytes = needed / 2 });

            Assert.Equal(ConvertStatus.NotEnoughSpace, outcome.Status);
            Assert.False(outcome.Succeeded);
            Assert.Equal(0, started);
            // Смысл проверки: не начинать нарезку, которая умрёт на середине.
            Assert.Empty(Directory.GetFiles(outFolder));
        }

        [Fact]
        public async Task NotEnoughSpace_CarriesEveryNumberTheMessageNeeds()
        {
            long sourceSize = 4096;

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource((int)sourceSize),
                    OutputFolder = Path.Combine(_root, "out"),
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 1024,
                    CheckDiskSpace = true
                },
                new FixedConflictPolicy(ConflictDecision.Overwrite), null, null, Log, null, CancellationToken.None,
                spaceCheck: (_, needed) => new SpaceCheckResult { RequiredBytes = needed, AvailableBytes = 1000 });

            // Диалог печатает все четыре числа — если хоть одно потеряется,
            // пользователь увидит «не хватает 0 байт».
            Assert.Equal(sourceSize, outcome.SourceSizeBytes);
            Assert.Equal(sourceSize, outcome.SpaceCheck.RequiredBytes);
            Assert.Equal(1000, outcome.SpaceCheck.AvailableBytes);
            Assert.Equal(sourceSize - 1000, outcome.SpaceCheck.MissingBytes);
        }

        [Fact]
        public async Task DiskSpaceCheckOff_CheckIsNotEvenConsulted()
        {
            bool consulted = false;

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(1024),
                    OutputFolder = Path.Combine(_root, "out"),
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 1024,
                    CheckDiskSpace = false
                },
                new FixedConflictPolicy(ConflictDecision.Overwrite), null, null, Log, null, CancellationToken.None,
                spaceCheck: (_, needed) =>
                {
                    consulted = true;
                    return new SpaceCheckResult { RequiredBytes = needed, AvailableBytes = 0 };
                });

            Assert.True(outcome.Succeeded);
            Assert.False(consulted);
        }

        // ============ Отмена ============

        [Fact]
        public async Task Cancelled_RemovesEveryFileItHadCreated()
        {
            string outFolder = Path.Combine(_root, "out");
            using var cts = new CancellationTokenSource();

            // Отменяем на первом же отчёте о прогрессе: часть файлов к этому моменту создана.
            var progress = new SyncProgress<SplitProgress>(_ => cts.Cancel());

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(6 * 1024 * 1024),
                    OutputFolder = outFolder,
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 512 * 1024
                },
                new FixedConflictPolicy(ConflictDecision.Overwrite),
                progress, null, Log, null, cts.Token);

            Assert.Equal(ConvertStatus.Cancelled, outcome.Status);
            Assert.False(outcome.Succeeded);

            // Недописанные части — худшее, что может остаться на диске: выглядят рабочими,
            // а данные в них неполные.
            Assert.Empty(Directory.GetFiles(outFolder));
            Assert.Contains(_log, l => l.Contains("езавершённых") || l.Contains("ncomplete"));
        }

        [Fact]
        public async Task CancelledBeforeAnyWork_ReportsCancelledWithNothingToClean()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await ConvertOperation.RunAsync(
                new ConvertRequest
                {
                    SourcePath = MakeSource(4096),
                    OutputFolder = Path.Combine(_root, "out"),
                    BaseFileName = "emmc.bin",
                    MaxPartSizeBytes = 1024
                },
                new FixedConflictPolicy(ConflictDecision.Overwrite), null, null, Log, null, cts.Token);

            Assert.Equal(ConvertStatus.Cancelled, outcome.Status);
        }

        // ============ Вспомогательное ============

        private string MakeSource(int sizeBytes)
        {
            string path = Path.Combine(_root, "source.bin");
            var bytes = new byte[sizeBytes];
            new Random(42).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private void Log(string line) => _log.Add(line);

        private Task<ConvertOutcome> RunAsync(ConvertRequest request, ConflictDecision onConflict = ConflictDecision.Overwrite) =>
            ConvertOperation.RunAsync(request, new FixedConflictPolicy(onConflict), null, null, Log, null, CancellationToken.None);

        private sealed class CountingResolver : IConflictResolver
        {
            private readonly ConflictDecision _decision;
            public int Calls { get; private set; }

            public CountingResolver(ConflictDecision decision) => _decision = decision;

            public Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName)
            {
                Calls++;
                return Task.FromResult(_decision);
            }

            public Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath)
            {
                Calls++;
                return Task.FromResult(_decision);
            }
        }
    }
}
