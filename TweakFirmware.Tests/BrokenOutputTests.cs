using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Что происходит, когда запись идти не может, а цепочка частей неполна.
    ///
    /// Оба случая до этих тестов заканчивались хуже, чем сама неисправность: нарезка
    /// подменяла причину сбоя своей собственной ошибкой, а сборка молча выдавала
    /// укороченный файл, который выглядит готовой прошивкой.
    /// </summary>
    public class BrokenOutputTests : IDisposable
    {
        private readonly string _tempDir;

        public BrokenOutputTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "TweakFirmwareBroken_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* временные файлы — не критично, если не удалились с первого раза */ }
        }

        private string CreateFile(string name, long sizeBytes)
        {
            string path = Path.Combine(_tempDir, name);
            var rnd = new Random(7);
            byte[] data = new byte[sizeBytes];
            rnd.NextBytes(data);
            File.WriteAllBytes(path, data);
            return path;
        }

        /// <summary>
        /// Следующую часть создать нельзя — её имя занято папкой. Наружу должна выйти
        /// настоящая причина, а не ObjectDisposedException: раньше блок finally вызывал
        /// Flush у потока, закрытого перед созданием новой части, и подменял ошибку.
        /// </summary>
        [Fact]
        public async Task Split_WhenNextPartCannotBeCreated_ReportsRealCause()
        {
            string source = CreateFile("source.bin", 4096);
            string outFolder = Path.Combine(_tempDir, "out");
            Directory.CreateDirectory(outFolder);

            // Имя первой части занимает папка: FileStream на такой путь не создаётся.
            Directory.CreateDirectory(Path.Combine(outFolder, "emmc.bin.part1"));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin",
                maxPartSizeBytes: 1024,
                verifyHash: false,
                progress: null,
                log: _ => { },
                ct: CancellationToken.None));

            Assert.IsNotType<ObjectDisposedException>(error);
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Ожидалась ошибка записи, получено {error.GetType().Name}: {error.Message}");
        }

        /// <summary>
        /// Часть в середине цепочки потеряна. Сборка обязана отказаться: собранный из
        /// оставшихся частей файл неотличим на вид от целой прошивки.
        /// </summary>
        [Fact]
        public void FindPartChain_WhenMiddlePartMissing_Throws()
        {
            string basePath = CreateFile("emmc.bin", 16);
            CreateFile("emmc.bin.part1", 16);
            // .part2 нет
            CreateFile("emmc.bin.part3", 16);

            var error = Assert.Throws<FileNotFoundException>(() => HashHelper.FindPartChain(basePath));

            Assert.Contains("2", error.Message);
            Assert.Contains("3", error.Message);
        }

        /// <summary>
        /// Обрыв в конце — не ошибка: цепочка из двух частей выглядит ровно так.
        /// Проверка на пропуск не должна ломать обычный случай.
        /// </summary>
        [Fact]
        public void FindPartChain_WhenChainSimplyEnds_ReturnsAllParts()
        {
            string basePath = CreateFile("emmc.bin", 16);
            CreateFile("emmc.bin.part1", 16);
            CreateFile("emmc.bin.part2", 16);

            var chain = HashHelper.FindPartChain(basePath);

            Assert.Equal(3, chain.Count);
            Assert.EndsWith("emmc.bin.part2", chain[2]);
        }

        /// <summary>
        /// Соседний файл с похожим именем — не часть цепочки. Иначе «emmc.bin.part1x»
        /// или чужой «emmc.bin.parts» превращали бы целую цепочку в разорванную.
        /// </summary>
        [Fact]
        public void FindPartChain_IgnoresNeighboursThatOnlyLookLikeParts()
        {
            string basePath = CreateFile("emmc.bin", 16);
            CreateFile("emmc.bin.part1", 16);
            CreateFile("emmc.bin.parts", 16);
            CreateFile("emmc.bin.part2x", 16);

            var chain = HashHelper.FindPartChain(basePath);

            Assert.Equal(2, chain.Count);
        }

        /// <summary>
        /// Крошечный лимит на огромном файле даёт больше частей, чем помещается в int.
        /// Раньше приведение молчало и давало отрицательное число: интерфейс показывал
        /// бессмыслицу, а нарезка начиналась.
        /// </summary>
        [Fact]
        public void CalculateExpectedPartCount_WhenCountExceedsInt_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FileSplitter.CalculateExpectedPartCount(long.MaxValue, 1024));
        }

        /// <summary>Обычные значения на границе int по-прежнему считаются.</summary>
        [Fact]
        public void CalculateExpectedPartCount_LargeButValid_Counts()
        {
            long limit = 4096;
            long size = (long)int.MaxValue * limit;

            Assert.Equal(int.MaxValue, FileSplitter.CalculateExpectedPartCount(size, limit));
        }
    }
}
