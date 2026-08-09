using System.IO;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>Разметки NAND с двоичной таблицей: Ambarella, Novatek и SAT-ресиверы.</summary>
    public class NandTableDetectorTests
    {
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

        // ---------- AMBARELLA ----------

        [Fact]
        public void Ambarella_ReadsTheShortFormatWithEightByteNames()
        {
            const long Table = 0x21000;
            const long Block = 0x20000;

            var builder = new DumpBuilder(0x100000);
            builder.Write(Table, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00);
            builder.WriteAscii(Table + 8, "bst");

            // Первая запись — сама сигнатура: смещение 0 блоков, размер 1 блок, имя bst.
            builder.WriteUInt32(Table + 0x00, 0);
            builder.WriteUInt32(Table + 0x04, 1);

            AddAmbarellaRecord(builder, Table, 1, 0x10, offsetBlocks: 2, sizeBlocks: 4, name: "pri");
            AddAmbarellaRecord(builder, Table, 2, 0x10, offsetBlocks: 6, sizeBlocks: 8, name: "rom");
            builder.WriteUInt32(Table + 3 * 0x10, 0x33219FBD);   // метка конца таблицы

            var (table, detected) = Run(new AmbarellaDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(3, table.Count);

            Assert.Equal("bst", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(Block, table[0].Length);

            Assert.Equal("pri", table[1].Name);
            Assert.Equal(2 * Block, table[1].Offset);
            Assert.Equal(4 * Block, table[1].Length);
        }

        [Fact]
        public void Ambarella_ReadsTheLongFormatWithThirtyTwoByteNames()
        {
            const long Table = 0x21000;

            var builder = new DumpBuilder(0x100000);
            builder.Write(Table, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00);
            builder.WriteAscii(Table + 8, "Bootstrap");

            builder.WriteUInt32(Table + 0x00, 0);
            builder.WriteUInt32(Table + 0x04, 1);

            AddAmbarellaRecord(builder, Table, 1, 0x28, offsetBlocks: 2, sizeBlocks: 4, name: "primary_firmware");
            builder.WriteUInt32(Table + 2 * 0x28, 0x33219FBD);

            var (table, detected) = Run(new AmbarellaDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(2, table.Count);
            Assert.Equal("primary_firmware", table[1].Name);
        }

        [Fact]
        public void Ambarella_SkipsRecordsOfZeroSize()
        {
            const long Table = 0x21000;

            var builder = new DumpBuilder(0x100000);
            builder.Write(Table, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00);
            builder.WriteAscii(Table + 8, "bst");
            builder.WriteUInt32(Table + 0x04, 1);

            AddAmbarellaRecord(builder, Table, 1, 0x10, offsetBlocks: 2, sizeBlocks: 0, name: "empty");
            AddAmbarellaRecord(builder, Table, 2, 0x10, offsetBlocks: 6, sizeBlocks: 8, name: "rom");
            builder.WriteUInt32(Table + 3 * 0x10, 0x33219FBD);

            var (table, _) = Run(new AmbarellaDetector(), builder.Build());

            Assert.DoesNotContain(table, p => p.Name == "empty");
            Assert.Contains(table, p => p.Name == "rom");
        }

        [Fact]
        public void Ambarella_StopsAtTheEndOfTheBlockEvenWithoutAnEndMarker()
        {
            // У повреждённой таблицы метки конца может не быть вовсе; перебор обязан
            // остановиться сам, а не уйти за прочитанный блок.
            var builder = new DumpBuilder(0x100000);
            builder.Write(0x21000, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00);
            builder.WriteAscii(0x21008, "bst");
            builder.Fill(0x21010, 0x3F0, 0x11);

            var (table, detected) = Run(new AmbarellaDetector(), builder.Build());

            Assert.True(detected);
            Assert.True(table.Count <= 0x400 / 0x10);
        }

        private static void AddAmbarellaRecord(
            DumpBuilder builder, long tableOffset, int index, int recordLength,
            uint offsetBlocks, uint sizeBlocks, string name)
        {
            long record = tableOffset + index * recordLength;
            builder.WriteUInt32(record, offsetBlocks);
            builder.WriteUInt32(record + 4, sizeBlocks);
            builder.WriteAscii(record + 8, name);
        }

        // ---------- NOVATEK ----------

        [Fact]
        public void Novatek_FindsTheTableAndReadsUntilTheNamesRunOut()
        {
            const long Table = 0x300000;

            var builder = new DumpBuilder(0x4000000);
            builder.WriteUInt64(Table, 0xE7F435B9F9BC3947);

            AddNovatekRecord(builder, Table, 0, "uboot", size: 0x200000, offset: 0);
            AddNovatekRecord(builder, Table, 1, "kernel", size: 0x400000, offset: 0x200000);

            var (table, detected) = Run(new NovatekDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(2, table.Count);

            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x200000, table[0].Length);

            Assert.Equal("kernel", table[1].Name);
            Assert.Equal(0x200000, table[1].Offset);
        }

        [Fact]
        public void Novatek_LooksNoFurtherThanTheFirstSixteenthOfTheDump()
        {
            // Сигнатура лежит слишком далеко — за границей области поиска.
            var builder = new DumpBuilder(0x1000000);
            builder.WriteUInt64(0x800000, 0xE7F435B9F9BC3947);

            Assert.False(Run(new NovatekDetector(), builder.Build()).Detected);
        }

        private static void AddNovatekRecord(DumpBuilder builder, long tableOffset, int index, string name, long size, long offset)
        {
            long record = tableOffset + 0x08 + index * 32;
            builder.WriteAscii(record, name);
            builder.WriteUInt64(record + 16, (ulong)size);
            builder.WriteUInt64(record + 24, (ulong)offset);
        }

        // ---------- SAT RECEIVER ----------

        [Fact]
        public void SatReceiver_ReadsBigEndianFields()
        {
            const long Table = 0x20000 - 0x200;

            var builder = new DumpBuilder(0x100000);
            builder.WriteUInt32(Table, 0xFADEBCAA);
            builder.Write(Table + 4, 2);

            AddSatRecord(builder, Table, 0, "boot", length: 0x100000, offset: 0);
            AddSatRecord(builder, Table, 1, "main", length: 0x200000, offset: 0x100000);

            var (table, detected) = Run(new SatReceiverDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(2, table.Count);

            Assert.Equal("boot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x100000, table[0].Length);

            Assert.Equal("main", table[1].Name);
            Assert.Equal(0x100000, table[1].Offset);
            Assert.Equal(0x200000, table[1].Length);
        }

        [Fact]
        public void SatReceiver_WithoutTheSignatureDetectsNothing()
        {
            var builder = new DumpBuilder(0x100000);
            builder.WriteUInt32(0x20000 - 0x200, 0x12345678);

            Assert.False(Run(new SatReceiverDetector(), builder.Build()).Detected);
        }

        private static void AddSatRecord(DumpBuilder builder, long tableOffset, int index, string name, uint length, uint offset)
        {
            long record = tableOffset + 5 + index * 0x18;
            builder.WriteAscii(record, name);

            // Поля записаны в обратном порядке байт.
            for (int i = 0; i < 4; i++) builder.Write(record + 0x08 + i, (byte)(length >> ((3 - i) * 8)));
            for (int i = 0; i < 4; i++) builder.Write(record + 0x10 + i, (byte)(offset >> ((3 - i) * 8)));
        }
    }
}
