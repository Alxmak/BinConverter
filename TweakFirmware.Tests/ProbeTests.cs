using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using TweakFirmware.Core.Operations;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Осмотр файла и цепочки для предпросмотра. Главное требование — не бросать
    /// исключений ни на каком вводе: эти вызовы идут по ходу набора пути, где путь
    /// заведомо бывает недописанным и неверным.
    /// </summary>
    public class ProbeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tfw-probe-" + Guid.NewGuid().ToString("N"));

        public ProbeTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        // ============ FileProbe ============

        [Fact]
        public void FileProbe_ReportsSizeOfAnExistingFile()
        {
            string path = Path.Combine(_root, "dump.bin");
            File.WriteAllBytes(path, new byte[4096]);

            var probe = FileProbe.Measure(path);

            Assert.True(probe.Exists);
            Assert.Equal(4096, probe.SizeBytes);
        }

        [Fact]
        public void FileProbe_MissingFileIsNotAnError()
        {
            var probe = FileProbe.Measure(Path.Combine(_root, "нет-такого.bin"));

            Assert.False(probe.Exists);
            Assert.Equal(0, probe.SizeBytes);
        }

        [Fact]
        public void FileProbe_FolderIsNotAFile()
        {
            var probe = FileProbe.Measure(_root);

            Assert.False(probe.Exists);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("C:")]
        [InlineData(@"недописанный\путь\")]
        [InlineData("файл|с|недопустимыми|знаками")]
        public void FileProbe_NeverThrowsOnAnyInput(string? path)
        {
            var probe = FileProbe.Measure(path);

            Assert.False(probe.Exists);
        }

        // ============ ChainProbe ============

        [Fact]
        public async Task ChainProbe_DescribesTheWholeChain()
        {
            string basePath = await MakeChain(4 * 1024, partSize: 1024);

            var probe = ChainProbe.Measure(basePath);

            Assert.True(probe.Resolved);
            Assert.Equal(4, probe.PartCount);
            Assert.Equal(4 * 1024, probe.TotalBytes);
            Assert.Equal("emmc.bin", probe.BaseFileName);
            Assert.Equal("emmc_merged.bin", probe.SuggestedOutputFileName);
            Assert.Equal("", probe.ErrorMessage);
        }

        [Fact]
        public async Task ChainProbe_WorksFromAnyPartNotJustTheBase()
        {
            string basePath = await MakeChain(4 * 1024, partSize: 1024);

            // Так же, как при перетаскивании: человек хватает любую часть.
            var probe = ChainProbe.Measure(basePath + ".part2");

            Assert.True(probe.Resolved);
            Assert.Equal(4, probe.PartCount);
            Assert.Equal("emmc_merged.bin", probe.SuggestedOutputFileName);
        }

        [Fact]
        public void ChainProbe_MissingFileGivesAReasonInsteadOfThrowing()
        {
            var probe = ChainProbe.Measure(Path.Combine(_root, "нет-такого.bin"));

            Assert.False(probe.Resolved);
            Assert.NotEqual("", probe.ErrorMessage);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ChainProbe_BlankPathIsSilent(string? path)
        {
            // Пустой путь — это не ошибка, а «ещё ничего не выбрали»: показывать
            // сообщение об ошибке в этот момент неправильно.
            var probe = ChainProbe.Measure(path);

            Assert.False(probe.Resolved);
            Assert.Equal("", probe.ErrorMessage);
        }

        [Theory]
        [InlineData("C:")]
        [InlineData(@"недописанный\путь\")]
        [InlineData("файл|с|недопустимыми|знаками")]
        public void ChainProbe_NeverThrowsOnAnyInput(string path)
        {
            var probe = ChainProbe.Measure(path);

            Assert.False(probe.Resolved);
        }

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
    }
}
