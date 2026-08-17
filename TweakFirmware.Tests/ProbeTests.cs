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

        // ============ FileProbeResult.SameFileAs ============
        //
        // Примета файла — размер и время записи. Проверяется она там, где результат
        // долгого чтения переживает само чтение: разбор дампа во «Извлечении разделов»
        // относится к содержимому, а не к имени. Путь при этом может не измениться —
        // файл под ним перезаписывают снаружи программы, и таблица разделов от прежнего
        // содержимого молча применилась бы к новому.

        [Fact]
        public void SameFileAs_TheSameFileMeasuredTwice_Matches()
        {
            string path = Path.Combine(_root, "same.bin");
            File.WriteAllBytes(path, new byte[1024]);

            Assert.True(FileProbe.Measure(path).SameFileAs(FileProbe.Measure(path)));
        }

        [Fact]
        public void SameFileAs_FileOfAnotherSize_DoesNotMatch()
        {
            string path = Path.Combine(_root, "grown.bin");
            File.WriteAllBytes(path, new byte[1024]);
            var before = FileProbe.Measure(path);

            File.WriteAllBytes(path, new byte[2048]);

            Assert.False(FileProbe.Measure(path).SameFileAs(before));
        }

        /// <summary>
        /// Главный случай: подменили содержимое, а размер остался прежним. Одного размера
        /// для приметы не хватает — дампы одной модели сплошь одинаковой длины.
        /// Время записи ставим сами: два быстрых подряд сохранения файловая система
        /// может пометить одним и тем же временем, и тест зависел бы от её точности.
        /// </summary>
        [Fact]
        public void SameFileAs_SameSizeButRewritten_DoesNotMatch()
        {
            string path = Path.Combine(_root, "swapped.bin");
            File.WriteAllBytes(path, new byte[1024]);
            var before = FileProbe.Measure(path);

            var other = new byte[1024];
            new Random(3).NextBytes(other);
            File.WriteAllBytes(path, other);
            File.SetLastWriteTimeUtc(path, before.LastWriteUtc.AddSeconds(5));

            var after = FileProbe.Measure(path);

            Assert.Equal(before.SizeBytes, after.SizeBytes);
            Assert.False(after.SameFileAs(before));
        }

        [Fact]
        public void SameFileAs_FileGoneFromDisk_DoesNotMatch()
        {
            string path = Path.Combine(_root, "vanished.bin");
            File.WriteAllBytes(path, new byte[1024]);
            var before = FileProbe.Measure(path);

            File.Delete(path);

            Assert.False(FileProbe.Measure(path).SameFileAs(before));
        }

        /// <summary>
        /// «Нечего читать» — это не «то же самое»: два несуществующих файла совпадающими
        /// не считаются, иначе проверка пропускала бы вперёд случай, когда осмотр
        /// не удался ни тогда, ни сейчас.
        /// </summary>
        [Fact]
        public void SameFileAs_TwoMissingFiles_DoNotMatch()
        {
            var missing = FileProbe.Measure(Path.Combine(_root, "нет-такого.bin"));

            Assert.False(missing.SameFileAs(missing));
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
