using System;
using System.IO;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Две разметки, у которых своя раскладка страницы: плееры Blu-ray и Dune HD.
    /// </summary>
    public class BluerayDuneDetectorTests
    {
        // ---------- BLUERAY ----------

        /// <summary>
        /// Собирает начало дампа так, как оно выглядит у этих плееров: двенадцать записей
        /// загрузчика, затем подстраницы, а за ними участок из байт 0xFF.
        /// </summary>
        private static byte[] BluerayImage(int subpages, int trailingFf, long size = 0x400000)
        {
            const int SubpageTotal = 0x420;

            var builder = new DumpBuilder(size);

            for (int i = 0; i < 12; i++)
                builder.WriteAscii(i * 0x40, "BOOTLOADER!");

            // Данные до конца последней подстраницы, затем хвост из единиц.
            long ffStart = (long)subpages * SubpageTotal;
            builder.Fill(ffStart, trailingFf, 0xFF);

            return builder.Build();
        }

        private static (PartitionTable Table, LayoutDetection? Detection, DumpContext Context) RunBlueray(byte[] image)
        {
            using var dump = new PlainDumpReader(new MemoryStream(image));
            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();
            var detector = new BluerayNandDetector();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            if (detection is not null)
                detector.ReadParts(detection, table, dump, context, host, CancellationToken.None);

            return (table, detection, context);
        }

        [Fact]
        public void Blueray_NeedsTheBootloaderMarkerInEveryRecord()
        {
            var image = BluerayImage(subpages: 2, trailingFf: 32);
            var (_, detection, _) = RunBlueray(image);

            Assert.NotNull(detection);
        }

        [Fact]
        public void Blueray_OneMissingMarkerIsEnoughToReject()
        {
            var image = BluerayImage(subpages: 2, trailingFf: 32);

            // Портим седьмую запись.
            Array.Clear(image, 6 * 0x40, 11);

            Assert.Null(RunBlueray(image).Detection);
        }

        [Theory]
        [InlineData(2, 32)]
        [InlineData(4, 16)]
        [InlineData(1, 64)]
        public void Blueray_WorksOutHowManySubpagesArePerPage(int subpages, int trailingFf)
        {
            var image = BluerayImage(subpages, trailingFf);
            var (_, detection, context) = RunBlueray(image);

            Assert.NotNull(detection);
            Assert.NotNull(context.BluerayLayout);
            Assert.Equal(subpages, context.BluerayLayout!.SubpagesPerPage);
            Assert.Equal(0x400, context.BluerayLayout.Main);
            Assert.Equal(0x20, context.BluerayLayout.Spare);
        }

        [Fact]
        public void Blueray_WithoutTheTrailingFfAreaTheLayoutStaysUnknown()
        {
            // Участок из байт 0xFF — единственный ориентир, по которому раскладка вообще
            // определяется. Без него оригинал выдавал бессмысленное число подстраниц;
            // здесь раскладка просто остаётся неизвестной, и дамп читается обычным
            // постраничным способом. Разметка при этом опознаётся: она держится на
            // записях загрузчика, а не на раскладке.
            var builder = new DumpBuilder(0x400000);
            for (int i = 0; i < 12; i++) builder.WriteAscii(i * 0x40, "BOOTLOADER!");

            var (_, detection, context) = RunBlueray(builder.Build());

            Assert.NotNull(detection);
            Assert.Null(context.BluerayLayout);
        }

        [Fact]
        public void Blueray_ReadsThePartitionTable()
        {
            var image = BluerayImage(subpages: 2, trailingFf: 32);
            var builder = new DumpBuilder(image.Length);
            builder.Write(0, image);

            const long Table = 0x200000;
            builder.Write(Table + 0x14, 2);   // число разделов

            AddBluerayRecord(builder, Table, 0, "uboot", offset: 0, length: 0x100000);
            AddBluerayRecord(builder, Table, 1, "kernel", offset: 0x100000, length: 0x200000);

            var (table, detection, _) = RunBlueray(builder.Build());

            Assert.NotNull(detection);
            Assert.Equal(2, table.Count);

            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x100000, table[0].Length);

            Assert.Equal("kernel", table[1].Name);
            Assert.Equal(0x100000, table[1].Offset);
            Assert.Equal(0x200000, table[1].Length);
        }

        private static void AddBluerayRecord(DumpBuilder builder, long table, int index, string name, uint offset, uint length)
        {
            long record = table + 0x40 + index * 0x40;
            builder.WriteAscii(record, name);
            builder.WriteUInt32(record + 0x18, offset);
            builder.WriteUInt32(record + 0x1C, length);
        }

        // ---------- DUNE HD ----------

        /// <summary>Дамп со страницами Dune HD и текстовым конфигом внутри.</summary>
        private static (PartitionTable Table, bool Detected, SilentAnalysisHost Host) RunDune(
            Action<DumpBuilder> writeConfig, DuneHdVariant variant = DuneHdVariant.Tv301)
        {
            var logical = new DumpBuilder(0x100000);
            writeConfig(logical);

            byte[] physical = DumpBuilder.AsDuneHd(logical.Build());

            var geometry = new NandGeometry(2048, 64, 0x20, 0x01, NandPageLayout.DuneHd, variant);
            using var dump = new NandDumpReader(new MemoryStream(physical), geometry);

            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();
            var detector = new DuneHdDetector();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            if (detection is null) return (table, false, host);

            detector.ReadParts(detection, table, dump, context, host, CancellationToken.None);
            return (table, true, host);
        }

        [Fact]
        public void DuneHd_ReadsThePartitionsFromTheTextConfig()
        {
            var (table, detected, host) = RunDune(config =>
            {
                config.WriteAscii(0x10, "product_id=HD_TV301");
                config.WriteAscii(0x40, "board_id=REV_C");

                config.WriteAscii(0x80, "_parts");
                config.WriteUInt16(0x80 + 7, 2);

                WriteDunePart(config, 0x100, 1, "uboot", offset: 0, size: 0x100000);
                WriteDunePart(config, 0x200, 2, "rootfs", offset: 0x100000, size: 0x400000);
            });

            Assert.True(detected);
            Assert.Equal(2, table.Count);

            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x100000, table[0].Length);

            Assert.Equal("rootfs", table[1].Name);
            Assert.Equal(0x100000, table[1].Offset);
            Assert.Equal(0x400000, table[1].Length);

            // Идентификаторы платы и изделия попадают в журнал: по ним подбирают прошивку.
            Assert.Contains(host.Messages, m => m.Message.Contains("HD_TV301"));
            Assert.Contains(host.Messages, m => m.Message.Contains("REV_C"));
        }

        [Fact]
        public void DuneHd_PartitionWithoutANameGetsANumberedOne()
        {
            var (table, detected, _) = RunDune(config =>
            {
                config.WriteAscii(0x80, "_parts");
                config.WriteUInt16(0x80 + 7, 1);

                config.WriteAscii(0x100, "_part1_offset");
                config.WriteUInt32(0x100 + 14, 0x200000);
                config.WriteAscii(0x140, "_part1_size");
                config.WriteUInt32(0x140 + 12, 0x100000);
                // Ключа с именем нет.
            });

            Assert.True(detected);
            Assert.Equal("part_1", table[0].Name);
        }

        [Fact]
        public void DuneHd_WithoutTheDuneHdPageLayoutDetectsNothing()
        {
            // Раскладку определяет разбор геометрии; без неё этот детектор не его дело.
            var logical = new DumpBuilder(0x100000);
            logical.WriteAscii(0x80, "_parts");

            using var dump = new PlainDumpReader(new MemoryStream(logical.Build()));
            var context = new DumpContext(dump);

            Assert.Null(new DuneHdDetector().TryDetect(dump, context, new SilentAnalysisHost(), CancellationToken.None));
        }

        [Fact]
        public void DuneHd_FindsTheLicenceFileInTheDump()
        {
            var logical = new DumpBuilder(0x100000);

            // Имя файла лежит по границе сектора, содержимое — через сектор от него.
            logical.WriteAscii(0x4000, "./dune_license.dlf");
            logical.WriteAscii(0x4200, "LICENSE-BODY-1234");

            using var dump = new PlainDumpReader(new MemoryStream(logical.Build()));

            var license = DuneHdDetector.FindLicense(dump);

            Assert.True(license.Found);
            Assert.Equal(0x4200, license.Offset);
            Assert.Equal("LICENSE-BODY-1234".Length, license.Length);
        }

        [Fact]
        public void DuneHd_NoLicenceInTheDumpIsNotAnError()
        {
            using var dump = new PlainDumpReader(new MemoryStream(new DumpBuilder(0x20000).Build()));

            Assert.False(DuneHdDetector.FindLicense(dump).Found);
        }

        private static void WriteDunePart(DumpBuilder config, long at, int index, string name, uint offset, uint size)
        {
            string offsetKey = $"_part{index}_offset";
            config.WriteAscii(at, offsetKey);
            config.WriteUInt32(at + offsetKey.Length + 1, offset);

            string sizeKey = $"_part{index}_size";
            config.WriteAscii(at + 0x40, sizeKey);
            config.WriteUInt32(at + 0x40 + sizeKey.Length + 1, size);

            string nameKey = $"_part{index}_name";
            config.WriteAscii(at + 0x80, nameKey);
            config.WriteAscii(at + 0x80 + nameKey.Length + 1, name);
        }
    }
}
