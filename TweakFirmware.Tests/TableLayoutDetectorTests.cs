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
    /// Разметки с двоичной таблицей разделов: MSTAR ANDROID, MPT AMLOGIC и MTK LG.
    /// </summary>
    public class TableLayoutDetectorTests
    {
        private const long Sector = 0x200;

        private static (PartitionTable Table, bool Detected) Run(ILayoutDetector detector, byte[] image)
        {
            using var dump = new PlainDumpReader(new MemoryStream(image));
            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            if (detection is null) return (table, false);

            detector.ReadParts(detection, table, dump, context, host, CancellationToken.None);
            return (table, true);
        }

        // ---------- MSTAR ANDROID ----------

        [Fact]
        public void ChinaAndroid_ReadsRecordsUntilTheSignatureStops()
        {
            var builder = new DumpBuilder(0x200000);

            // Каждая запись занимает целый сектор и начинается своей сигнатурой.
            AddChinaRecord(builder, 0, addrSectors: 0x1000, sizeSectors: 0x800, name: "MBOOT");
            AddChinaRecord(builder, 1, addrSectors: 0x1800, sizeSectors: 0x400, name: "MPOOL");
            AddChinaRecord(builder, 2, addrSectors: 0x1C00, sizeSectors: 0x400, name: "misc");

            var (table, detected) = Run(new ChinaAndroidDetector(), builder.Build());

            Assert.True(detected);

            // Первая запись порождает ещё и раздел с самой таблицей.
            Assert.Equal("Partitions_table", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x1000 * Sector, table[0].Length);

            Assert.Equal("MBOOT", table[1].Name);
            Assert.Equal(0x1000 * Sector, table[1].Offset);
            Assert.Equal(0x800 * Sector, table[1].Length);

            Assert.Equal("misc", table[3].Name);
            Assert.Equal(4, table.Count);
        }

        [Fact]
        public void ChinaAndroid_NeedsTheSignatureInTheFirstRecord()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt16(0x200, 0x1234);

            Assert.False(Run(new ChinaAndroidDetector(), builder.Build()).Detected);
        }

        private static void AddChinaRecord(DumpBuilder builder, int index, uint addrSectors, uint sizeSectors, string name)
        {
            long record = 0x200 + index * 0x200;
            builder.WriteUInt16(record, 0x5840);
            builder.WriteUInt32(record + 0x08, addrSectors);
            builder.WriteUInt32(record + 0x0C, sizeSectors);
            builder.WriteAscii(record + 0x10, name);
        }

        // ---------- MPT AMLOGIC ----------

        [Fact]
        public void MptAmlogic_ReadsTheRecordCountAndEveryRecord()
        {
            var builder = new DumpBuilder(0x2500000);
            const long Table = 0x2400000;

            builder.WriteAscii(Table, "MPT");
            builder.Write(Table + 3, 0x00);
            builder.Write(Table + 0x10, 3);   // число разделов

            AddMptRecord(builder, Table, 0, "boot", 0x100000, 0x2000000);
            AddMptRecord(builder, Table, 1, "system", 0x400000, 0x2100000);
            AddMptRecord(builder, Table, 2, "data", 0x800000, 0x2500000);

            var (table, detected) = Run(new MptAmlogicDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(3, table.Count);

            Assert.Equal("boot", table[0].Name);
            Assert.Equal(0x2000000, table[0].Offset);
            Assert.Equal(0x100000, table[0].Length);

            Assert.Equal("data", table[2].Name);
            Assert.Equal(0x2500000, table[2].Offset);
        }

        [Fact]
        public void MptAmlogic_WithoutTheSignatureDetectsNothing()
        {
            var builder = new DumpBuilder(0x2500000);
            builder.WriteAscii(0x2400000, "XXX");

            Assert.False(Run(new MptAmlogicDetector(), builder.Build()).Detected);
        }

        private static void AddMptRecord(DumpBuilder builder, long tableOffset, int index, string name, long length, long offset)
        {
            long record = tableOffset + 0x18 + index * 0x28;
            builder.WriteAscii(record, name);
            builder.WriteUInt64(record + 0x10, (ulong)length);
            builder.WriteUInt64(record + 0x18, (ulong)offset);
        }

        // ---------- MTK LG ----------

        [Fact]
        public void MtkLg_ReadsTheNewerTableWithSixtyFourBitFields()
        {
            var builder = new DumpBuilder(0x200000);
            const long Table = 0x100000;

            builder.WriteUInt32(Table, 0x20120716);
            builder.Write(Table + 0x0C, 2);
            builder.WriteAscii(Table + 0x10, "mtk_lg_map");

            AddMtkLgWideRecord(builder, Table, 0, "uboot", 0, 0x200000, "boot loader");
            AddMtkLgWideRecord(builder, Table, 1, "kernel", 0x200000, 0x400000, "");

            var (table, detected) = Run(new MtkLgDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(2, table.Count);

            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x200000, table[0].Length);
            Assert.Equal("boot loader", table[0].Comment);

            Assert.Equal("kernel", table[1].Name);
            Assert.Equal(0x200000, table[1].Offset);
        }

        [Theory]
        [InlineData(0x20110620u)]
        [InlineData(0x20110609u)]
        public void MtkLg_ReadsTheOlderTablesWithThirtyTwoBitFields(uint signature)
        {
            var builder = new DumpBuilder(0x200000);
            const long Table = 0x50000;

            builder.WriteUInt32(Table, signature);
            builder.Write(Table + 0x0C, 2);
            builder.WriteAscii(Table + 0x10, "lg_map");

            AddMtkLgNarrowRecord(builder, Table, 0, "uboot", 0, 0x200000, "enc");
            AddMtkLgNarrowRecord(builder, Table, 1, "kernel", 0x200000, 0x400000, "");

            var (table, detected) = Run(new MtkLgDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(2, table.Count);
            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0x200000, table[0].Length);
            Assert.Equal("enc", table[0].Comment);
            Assert.Equal(0x200000, table[1].Offset);
        }

        [Fact]
        public void MtkLg_WithoutAKnownSignatureDetectsNothing()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0x100000, 0x20990101);   // дата есть, но не та

            Assert.False(Run(new MtkLgDetector(), builder.Build()).Detected);
        }

        private static void AddMtkLgWideRecord(
            DumpBuilder builder, long tableOffset, int index, string name, long offset, long length, string comment)
        {
            long record = tableOffset + 0x50 + index * 0x60;
            builder.WriteAscii(record, name);
            builder.WriteUInt64(record + 0x20, (ulong)offset);
            builder.WriteUInt64(record + 0x28, (ulong)length);
            if (comment.Length > 0) builder.WriteAscii(record + 0x30, comment);
        }

        private static void AddMtkLgNarrowRecord(
            DumpBuilder builder, long tableOffset, int index, string name, long offset, long length, string comment)
        {
            long record = tableOffset + 0x48 + index * 0x58;
            builder.WriteAscii(record, name);
            builder.WriteUInt32(record + 0x20, (uint)offset);
            builder.WriteUInt32(record + 0x24, (uint)length);
            if (comment.Length > 0) builder.WriteAscii(record + 0x28, comment);
        }
    }
}
