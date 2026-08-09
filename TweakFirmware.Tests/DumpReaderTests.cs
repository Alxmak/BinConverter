using System;
using System.IO;
using TweakFirmware.Core.Dump;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Чтение дампа в логических адресах. Главная проверка одна и та же для всех
    /// раскладок: что бы ни лежало в файле, читатель обязан отдать ровно тот массив,
    /// из которого этот файл был собран.
    /// </summary>
    public class DumpReaderTests
    {
        [Fact]
        public void PlainReader_ReturnsExactlyWhatWasWritten()
        {
            byte[] data = new DumpBuilder(4096).Pattern(0, 4096).Build();

            using var reader = new PlainDumpReader(new MemoryStream(data));

            Assert.Equal(4096, reader.PhysicalSize);
            Assert.Equal(4096, reader.LogicalSize);
            Assert.Null(reader.Geometry);
            Assert.Equal(data[100..200], reader.ReadBlock(100, 100));
        }

        [Fact]
        public void PlainReader_ClampsAtTheEndInsteadOfThrowing()
        {
            // Детекторы щупают фиксированные адреса вслепую, поэтому выход за конец дампа —
            // штатная ситуация, а не ошибка: недочитанный хвост просто обнуляется.
            byte[] data = new DumpBuilder(1000, fill: 0x5A).Build();
            using var reader = new PlainDumpReader(new MemoryStream(data));

            var buffer = new byte[100];
            int read = reader.Read(960, buffer);

            Assert.Equal(40, read);
            Assert.All(buffer[40..], b => Assert.Equal(0, b));
            Assert.Equal(0, reader.Read(5000, buffer));
        }

        [Theory]
        [InlineData(2048, 64)]
        [InlineData(4096, 224)]
        [InlineData(512, 16)]
        public void NandReader_HidesSpareCompletely(int main, int spare)
        {
            byte[] logical = new DumpBuilder(main * 9).Pattern(0, main * 9).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main, spare);

            var geometry = new NandGeometry(main, spare, main / 64, Math.Max(1, spare / 64));
            using var reader = new NandDumpReader(new MemoryStream(physical), geometry);

            Assert.Equal(physical.Length, reader.PhysicalSize);
            Assert.Equal(logical.Length, reader.LogicalSize);
            Assert.Equal(logical, reader.ReadBlock(0, logical.Length));
        }

        [Fact]
        public void NandReader_ReadsAcrossPageBoundariesAndFromOddOffsets()
        {
            const int Main = 2048, Spare = 64;

            byte[] logical = new DumpBuilder(Main * 8).Pattern(0, Main * 8).Build();
            byte[] physical = DumpBuilder.AsNand(logical, Main, Spare);

            var geometry = new NandGeometry(Main, Spare, 0x20, 0x01);
            using var reader = new NandDumpReader(new MemoryStream(physical), geometry);

            // Смещения и длины подобраны так, чтобы чтения заведомо пересекали границы
            // страниц и начинались в середине области данных — там, где ошибка в пересчёте
            // адреса сразу даёт сдвиг на размер spare.
            foreach (int offset in new[] { 0, 1, 2047, 2048, 2049, 4095, 5000 })
            {
                foreach (int length in new[] { 1, 63, 2048, 2113, 5000 })
                {
                    if (offset + length > logical.Length) continue;
                    Assert.Equal(logical[offset..(offset + length)], reader.ReadBlock(offset, length));
                }
            }
        }

        [Fact]
        public void NandReader_HandlesTheDuneHdPageLayout()
        {
            byte[] logical = new DumpBuilder(2048 * 6).Pattern(0, 2048 * 6).Build();
            byte[] physical = DumpBuilder.AsDuneHd(logical);

            var geometry = new NandGeometry(2048, 64, 0x20, 0x01, NandPageLayout.DuneHd, DuneHdVariant.Tv301);
            using var reader = new NandDumpReader(new MemoryStream(physical), geometry);

            Assert.Equal(logical, reader.ReadBlock(0, logical.Length));

            // Внутри страницы Dune HD данные разорваны на пять кусков; чтение, попадающее
            // на стык, — самый вероятный способ всё сломать.
            Assert.Equal(logical[500..1100], reader.ReadBlock(500, 600));
            Assert.Equal(logical[1530..2100], reader.ReadBlock(1530, 570));
        }

        [Fact]
        public void NandReader_BluerayLayoutSplitsThePageIntoSubpages()
        {
            // Подстраница 1024 + 32, две подстраницы на страницу: физическая страница
            // 2112 байт, полезных 2048 — та же геометрия, что у обычной 2048 + 64.
            const int SubMain = 1024, SubSpare = 32, Subpages = 2;

            byte[] logical = new DumpBuilder(2048 * 5).Pattern(0, 2048 * 5).Build();

            int pages = logical.Length / 2048;
            var physical = new byte[pages * 2112];
            Array.Fill(physical, (byte)0xFF);
            for (int page = 0; page < pages; page++)
                for (int sub = 0; sub < Subpages; sub++)
                    Array.Copy(logical, page * 2048 + sub * SubMain,
                               physical, page * 2112 + sub * (SubMain + SubSpare), SubMain);

            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);
            using var reader = new NandDumpReader(new MemoryStream(physical), geometry)
            {
                Blueray = new BluerayPageLayout(SubMain, SubSpare, Subpages, TrailingFf: 0)
            };

            Assert.Equal(logical, reader.ReadBlock(0, logical.Length));
        }

        [Fact]
        public void CachingReader_ReturnsTheSameBytesAsTheReaderBelowIt()
        {
            byte[] logical = new DumpBuilder(2048 * 40).Pattern(0, 2048 * 40).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main: 2048, spare: 64);
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            using var direct = new NandDumpReader(new MemoryStream(physical), geometry);
            using var cached = new CachingDumpReader(
                new NandDumpReader(new MemoryStream(physical), geometry), windowSize: 4096);

            // Окно кэша нарочно меньше дампа, а обращения идут вразнобой — так проверяется
            // и попадание в окно, и промах, и запрос больше самого окна.
            foreach (int offset in new[] { 0, 100, 5000, 100, 70000, 4096, 0, 81000 })
            {
                foreach (int length in new[] { 16, 4095, 4096, 9000 })
                {
                    if (offset + length > logical.Length) continue;
                    Assert.Equal(direct.ReadBlock(offset, length), cached.ReadBlock(offset, length));
                }
            }
        }

        [Fact]
        public void CachingReader_ResetIsNeededAfterTheLayoutChanges()
        {
            byte[] logical = new DumpBuilder(2048 * 4).Pattern(0, 2048 * 4).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main: 2048, spare: 64);
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            var inner = new NandDumpReader(new MemoryStream(physical), geometry);
            using var cached = new CachingDumpReader(inner, windowSize: 8192);

            Assert.Equal(logical[0..64], cached.ReadBlock(0, 64));

            cached.Reset();
            Assert.Equal(logical[0..64], cached.ReadBlock(0, 64));
        }

        [Fact]
        public void Contains_AnswersWhetherTheRangeFitsInTheDump()
        {
            byte[] logical = new DumpBuilder(2048 * 4).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main: 2048, spare: 64);
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            using var reader = new NandDumpReader(new MemoryStream(physical), geometry);

            Assert.True(reader.Contains(0, 2048 * 4));
            Assert.False(reader.Contains(0, 2048 * 4 + 1));
            Assert.False(reader.Contains(-1, 10));
        }

        [Fact]
        public void ReadRawPage_GivesBackMainAndSpareTogether()
        {
            byte[] logical = new DumpBuilder(2048 * 3).Pattern(0, 2048 * 3).Build();
            byte[] physical = DumpBuilder.AsNand(logical, main: 2048, spare: 64, spareFill: 0xAB);
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            using var reader = new NandDumpReader(new MemoryStream(physical), geometry);

            Assert.Equal(3, reader.PageCount);

            var page = new byte[geometry.Page];
            reader.ReadRawPage(1, page);

            Assert.Equal(logical[2048..4096], page[..2048]);
            Assert.All(page[2048..], b => Assert.Equal(0xAB, b));
        }
    }
}
