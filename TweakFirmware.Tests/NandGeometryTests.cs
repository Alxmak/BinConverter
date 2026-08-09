using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Определение геометрии страницы NAND. Проверяются оба шага — соотношение по размеру
    /// файла и размер страницы по статистике 0xFF, — а также перевод адресов между
    /// логической и физической системами координат.
    /// </summary>
    public class NandGeometryTests
    {
        [Theory]
        [InlineData(0x010, 0x01)]
        [InlineData(0x020, 0x01)]
        [InlineData(0x040, 0x05)]
        [InlineData(0x080, 0x07)]
        [InlineData(0x100, 0x0F)]
        public void TryDetectRatio_RecognisesEveryKnownRatio(int relativeMain, int relativeSpare)
        {
            // Размер дампа NAND — это (main + spare), умноженное на степень двойки.
            long size = DumpBuilder.NandFileSize(relativeMain, relativeSpare, pages: 1 << 20);

            var ratio = NandGeometryDetector.TryDetectRatio(size);

            Assert.NotNull(ratio);
            Assert.Equal(relativeMain, ratio!.Value.RelativeMain);
            Assert.Equal(relativeSpare, ratio.Value.RelativeSpare);
        }

        [Theory]
        [InlineData(0x100000000)]   // ровно 4 ГиБ — типичный eMMC
        [InlineData(0x1000000)]     // 16 МиБ — NOR
        [InlineData(0x2000)]        // 8 КиБ — EEPROM 24C64
        [InlineData(0)]
        public void TryDetectRatio_RejectsSizesThatAreNotNand(long size)
        {
            Assert.Null(NandGeometryDetector.TryDetectRatio(size));
        }

        [Fact]
        public void TryDetectRatio_KnowsTheK9Gag08U0ESpecialCase()
        {
            // Эта микросхема не укладывается в общее правило, поэтому её размер прописан
            // в детекторе отдельно — так же, как в оригинальном скрипте.
            var ratio = NandGeometryDetector.TryDetectRatio(0x88A7D800);

            Assert.NotNull(ratio);
            Assert.Equal(0x800, ratio!.Value.RelativeMain);
            Assert.Equal(0x6D, ratio.Value.RelativeSpare);
        }

        [Theory]
        [InlineData(2048, 64)]
        [InlineData(4096, 224)]
        [InlineData(8192, 480)]
        public void AddSpareAndMinusSpare_AreInverseOfEachOther(int main, int spare)
        {
            var geometry = new NandGeometry(main, spare, main / 64, spare / 64 == 0 ? 1 : spare / 64);

            for (long logical = 0; logical < main * 10L; logical += 137)
                Assert.Equal(logical, geometry.MinusSpare(geometry.AddSpare(logical)));
        }

        [Fact]
        public void AddSpare_SkipsExactlyOneSpareAreaPerPage()
        {
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            Assert.Equal(0, geometry.AddSpare(0));
            Assert.Equal(2047, geometry.AddSpare(2047));
            Assert.Equal(2112, geometry.AddSpare(2048));      // начало второй страницы
            Assert.Equal(2112 + 100, geometry.AddSpare(2048 + 100));
        }

        [Fact]
        public void AddSpare_RejectsNegativeOffsets()
        {
            // Длина -1 («до конца дампа») обязана быть развёрнута раньше — на шаге
            // вставки промежутков. Если она дошла сюда, это ошибка в порядке вызовов.
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.AddSpare(-1));
        }

        [Fact]
        public void TryDetectPageSize_FindsThePageOfASynthesisedNandImage()
        {
            var logical = new DumpBuilder(2048 * 16).Pattern(0, 2048 * 16).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main: 2048, spare: 64);

            using var stream = new MemoryStream(physical);
            var geometry = NandGeometryDetector.TryDetectPageSize(stream, 0x20, 0x01);

            Assert.NotNull(geometry);
            Assert.Equal(2048, geometry!.Main);
            Assert.Equal(64, geometry.Spare);
            Assert.Equal(2112, geometry.Page);
        }

        [Fact]
        public void TryDetectPageSize_GivesUpOnAnImageWithoutSpareAreas()
        {
            // Сплошные данные без служебных областей: статистика 0xFF ничего не подскажет.
            var flat = new DumpBuilder(2048 * 32).Pattern(0, 2048 * 32).Build();

            using var stream = new MemoryStream(flat);
            Assert.Null(NandGeometryDetector.TryDetectPageSize(stream, 0x20, 0x01));
        }

        [Fact]
        public void BuildManualOptions_OffersEightPageSizesPlusNoSpare()
        {
            var options = NandGeometryDetector.BuildManualOptions(0x20, 0x01);

            Assert.Equal(9, options.Count);
            Assert.Equal(256, options[0].Main);
            Assert.Equal(32768, options[7].Main);
            Assert.True(options[8].IsNoSpare);

            for (int i = 0; i < options.Count; i++)
                Assert.Equal(i, options[i].Index);
        }

        [Fact]
        public void FromManualChoice_BuildsTheGeometryTheUserPicked()
        {
            var geometry = NandGeometryDetector.FromManualChoice(3, 0x20, 0x01);

            Assert.NotNull(geometry);
            Assert.Equal(2048, geometry!.Main);
            Assert.Equal(64, geometry.Spare);
        }

        [Fact]
        public void FromManualChoice_NoSpareMeansTreatTheDumpAsFlat()
        {
            Assert.Null(NandGeometryDetector.FromManualChoice(8, 0x20, 0x01));
        }

        [Fact]
        public void DetectDuneHd_RecognisesTheTv301Marker()
        {
            var logical = new DumpBuilder(2048 * 12).Pattern(0, 2048 * 12).Build();
            byte[] physical = DumpBuilder.AsDuneHd(logical, marker: 0xAAAAAAAA);

            using var stream = new MemoryStream(physical);
            Assert.Equal(DuneHdVariant.Tv301, NandGeometryDetector.DetectDuneHd(stream, 0x840));
        }

        [Fact]
        public void DetectDuneHd_RecognisesTheTv102Marker()
        {
            var logical = new DumpBuilder(2048 * 12).Pattern(0, 2048 * 12).Build();
            byte[] physical = DumpBuilder.AsDuneHd(logical, marker: 0xFFAAFFFF);

            using var stream = new MemoryStream(physical);
            Assert.Equal(DuneHdVariant.Tv102, NandGeometryDetector.DetectDuneHd(stream, 0x840));
        }

        [Fact]
        public void DetectDuneHd_StaysSilentOnAnOrdinaryNandDump()
        {
            var logical = new DumpBuilder(2048 * 12).Pattern(0, 2048 * 12).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main: 2048, spare: 64);

            using var stream = new MemoryStream(physical);
            Assert.Equal(DuneHdVariant.None, NandGeometryDetector.DetectDuneHd(stream, 2112));
        }

        [Fact]
        public async Task DetectAsync_IgnoresDumpsSmallerThanTheThreshold()
        {
            // Порог из оригинала — 64 МиБ. Всё, что меньше, разбирается как сплошной дамп,
            // даже если размер случайно похож на NAND.
            using var stream = new MemoryStream(new byte[1024]);
            var host = new SilentAnalysisHost();

            Assert.Null(await NandGeometryDetector.DetectAsync(stream, host, CancellationToken.None));
            Assert.Empty(host.Messages);
        }

        [Fact]
        public async Task DetectAsync_DetectsGeometryOnAFullSizedNandDump()
        {
            // 0x4200000 = 0x21 << 21: соотношение 2048 + 64 и размер выше порога в 64 МиБ.
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                var logical = new DumpBuilder(2048 * 16).Pattern(0, 2048 * 16).Build();
                byte[] head = DumpBuilder.AsNand(logical, main: 2048, spare: 64);

                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    // Файл создаётся разреженным: интересен только его начало и размер.
                    file.SetLength(0x4200000);
                    file.Position = 0;
                    file.Write(head);
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                var host = new SilentAnalysisHost();

                var geometry = await NandGeometryDetector.DetectAsync(stream, host, CancellationToken.None);

                Assert.NotNull(geometry);
                Assert.Equal(2048, geometry!.Main);
                Assert.Equal(64, geometry.Spare);
                Assert.Equal(NandPageLayout.Standard, geometry.Layout);
                Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Found);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task DetectAsync_AsksTheUserWhenTheHeuristicFails()
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                    file.SetLength(0x4200000);   // сплошные нули: статистика 0xFF пуста

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);

                // Пользователь выбирает вариант 3 — страница 2048 + 64.
                var host = new SilentAnalysisHost(geometryChoice: 3);
                var geometry = await NandGeometryDetector.DetectAsync(stream, host, CancellationToken.None);

                Assert.NotNull(geometry);
                Assert.Equal(2048, geometry!.Main);
                Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task DetectAsync_UserRefusalMeansTheDumpIsReadAsFlat()
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                    file.SetLength(0x4200000);

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                var host = new SilentAnalysisHost(geometryChoice: null);

                Assert.Null(await NandGeometryDetector.DetectAsync(stream, host, CancellationToken.None));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
