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
    /// Две разметки NAND, у которых кроме разделов читается ещё и таблица переназначения
    /// плохих блоков: LG и Samsung.
    /// </summary>
    public class LgSamsungNandDetectorTests
    {
        private const int Main = 2048;
        private const int Spare = 64;

        private static NandGeometry Geometry() => new(Main, Spare, 0x20, 0x01);

        /// <summary>
        /// Собирает дамп NAND из логического образа и прогоняет по нему детектор — то есть
        /// проверяет заодно, что чтение сквозь служебные области не сбивает смещения.
        /// </summary>
        private static (PartitionTable Table, bool Detected, DumpContext Context) RunOnNand(
            ILayoutDetector detector, byte[] logical)
        {
            byte[] physical = DumpBuilder.AsNand(logical, Main, Spare);

            using var dump = new NandDumpReader(new MemoryStream(physical), Geometry());
            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            if (detection is null) return (table, false, context);

            detector.ReadParts(detection, table, dump, context, host, CancellationToken.None);
            return (table, true, context);
        }

        // ---------- LG NAND ----------

        private static void AddLgRecord(DumpBuilder builder, long table, int index, string name, uint offset, uint length, string comment)
        {
            long record = table + 0xF0 + index * 0x54;
            builder.WriteAscii(record, name);
            builder.WriteUInt32(record + 0x20, offset);
            builder.WriteUInt32(record + 0x24, length);
            if (comment.Length > 0) builder.WriteAscii(record + 0x28, comment);
        }

        [Theory]
        [InlineData("mstar_map0")]
        [InlineData("bcm35230_map0")]
        public void LgNand_ReadsTheStraightforwardMaps(string mapName)
        {
            const long Table = 0x40000;

            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(Table + 0x10, mapName);
            builder.Write(Table + 0x0D, 2);

            AddLgRecord(builder, Table, 0, "bbminfo", 0x100000, 0x20000, "");
            AddLgRecord(builder, Table, 1, "kernel", 0x120000, 0x200000, "boot");

            var (table, detected, _) = RunOnNand(new LgNandDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(2, table.Count);
            Assert.Equal("bbminfo", table[0].Name);
            Assert.Equal(0x100000, table[0].Offset);
            Assert.Equal("kernel", table[1].Name);
            Assert.Equal(0x200000, table[1].Length);
            Assert.Equal("boot", table[1].Comment);
        }

        [Fact]
        public void LgNand_ReadsTheBcm35xxMapWhereTheWholeTableIsByteSwapped()
        {
            const long Table = 0x40000;

            // У этой карты вся таблица записана обратными четвёрками байт, поэтому в дамп
            // кладём уже перевёрнутое — детектор обязан вернуть всё на место.
            //
            // Имя карты при этом тоже переворачивается: «bcm35xx_map0», разбитое на
            // четвёрки и перевёрнутое, читается как «3mcb_xx50pam». Именно эту, уже
            // перевёрнутую строку детектор и ищет — по ней он и узнаёт, что таблицу
            // нужно разворачивать.
            var straight = new DumpBuilder(0x10000);
            straight.WriteAscii(0x10, "bcm35xx_map0");
            straight.Write(0x0D, 1);
            AddLgRecord(straight, 0, 0, "uboot", 0x100000, 0x40000, "");

            byte[] swapped = SwapEveryDword(straight.Build(), 0x2000);
            Assert.Equal("3mcb_xx50pam", System.Text.Encoding.ASCII.GetString(swapped, 0x10, 12));

            var builder = new DumpBuilder(0x400000);
            builder.Write(Table, swapped);

            var (table, detected, _) = RunOnNand(new LgNandDetector(), builder.Build());

            Assert.True(detected);
            Assert.Single(table.Items);
            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0x100000, table[0].Offset);
            Assert.Equal(0x40000, table[0].Length);
        }

        [Fact]
        public void LgNand_ReadsTheDouglasMapShiftedByFourBytes()
        {
            const long Table = 0x60000;

            // У Douglas имя карты стоит на 0x0C вместо 0x10, то есть вся таблица сдвинута
            // на четыре байта влево; числа вдобавок записаны в обратном порядке байт.
            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(Table + 0x0C, "douglas_map0");
            builder.Write(Table + 0x09, 1);                       // счётчик, тоже сдвинут

            long record = Table + 0xF0 - 4;
            builder.WriteAscii(record, "reserved");
            WriteUInt32BigEndian(builder, record + 0x20, 0x100000);
            WriteUInt32BigEndian(builder, record + 0x24, 0x40000);

            var (table, detected, _) = RunOnNand(new LgNandDetector(), builder.Build());

            Assert.True(detected);
            Assert.Single(table.Items);
            Assert.Equal("reserved", table[0].Name);
            Assert.Equal(0x100000, table[0].Offset);
            Assert.Equal(0x40000, table[0].Length);
        }

        [Fact]
        public void LgNand_ReadsTheBadBlockTableFromTheBbminfoPartition()
        {
            const long Table = 0x40000;
            const long BbmInfo = 0x100000;

            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(Table + 0x10, "mstar_map0");
            builder.Write(Table + 0x0D, 1);
            AddLgRecord(builder, Table, 0, "bbminfo", (uint)BbmInfo, 0x20000, "");

            // Сигнатура таблицы, число плохих блоков и пары «куда/откуда».
            builder.WriteUInt32(BbmInfo, 0xA6A59281);
            builder.WriteUInt32(BbmInfo + 4, 2);
            builder.WriteUInt32(BbmInfo + 8, 0x3FF);      // резервный блок: по нему считается объём
            builder.WriteUInt32(BbmInfo + 12, 17);
            builder.WriteUInt32(BbmInfo + 16, 0x3FE);
            builder.WriteUInt32(BbmInfo + 20, 42);

            var (_, detected, context) = RunOnNand(new LgNandDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(new long[] { 17, 42 }, context.BadBlocks);

            // Номер резервного блока 0x3FF округляется вверх до 0x400 блоков в микросхеме.
            Assert.Equal(context.Dump.LogicalSize / 0x400, context.NandBlockSize);
        }

        [Fact]
        public void LgNand_WithoutAKnownMapNameDetectsNothing()
        {
            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(0x40000 + 0x10, "some_other_map");

            Assert.False(RunOnNand(new LgNandDetector(), builder.Build()).Detected);
        }

        private static byte[] SwapEveryDword(byte[] data, int length)
        {
            var result = new byte[length];
            Array.Copy(data, result, Math.Min(length, data.Length));

            for (int i = 0; i + 4 <= length; i += 4)
            {
                (result[i], result[i + 3]) = (result[i + 3], result[i]);
                (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
            }

            return result;
        }

        private static void WriteUInt32BigEndian(DumpBuilder builder, long offset, uint value)
        {
            for (int i = 0; i < 4; i++) builder.Write(offset + i, (byte)(value >> ((3 - i) * 8)));
        }

        // ---------- SAMSUNG NAND ----------

        [Fact]
        public void SamsungNand_FindsTheMarkersFromTheEndAndReadsTheTable()
        {
            // Метки лежат в конце дампа, на расстоянии одного блока друг от друга.
            const long BlockSize = 0x20000;
            const long Upch = 0x300000;
            const long Lpch = Upch + BlockSize;

            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(Upch, "UPCH");
            builder.WriteAscii(Lpch, "LPCH");

            builder.WriteAscii(Lpch + 0x200, "FSRPART");
            builder.Write(Lpch + 0x200 + 0x0C, 2);

            AddSamsungRecord(builder, Lpch + 0x200, 0, type: 0x11, offsetBlocks: 0, sizeBlocks: 4);
            AddSamsungRecord(builder, Lpch + 0x200, 1, type: 0x22, offsetBlocks: 4, sizeBlocks: 8);

            var (table, detected, context) = RunOnNand(new SamsungNandDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(BlockSize, context.NandBlockSize);
            Assert.Equal(2, table.Count);

            // Своих имён у разделов нет — имя собирается из номера и типа.
            Assert.Equal("part_00_11", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(4 * BlockSize, table[0].Length);

            Assert.Equal("part_01_22", table[1].Name);
            Assert.Equal(4 * BlockSize, table[1].Offset);
        }

        [Fact]
        public void SamsungNand_ReadsBadBlocksUntilTheEndMarker()
        {
            const long BlockSize = 0x20000;
            const long Upch = 0x300000;
            const long Lpch = Upch + BlockSize;

            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(Upch, "UPCH");
            builder.WriteAscii(Lpch, "LPCH");
            builder.WriteAscii(Lpch + 0x200, "FSRPART");
            builder.Write(Lpch + 0x200 + 0x0C, 1);
            AddSamsungRecord(builder, Lpch + 0x200, 0, type: 0x11, offsetBlocks: 0, sizeBlocks: 4);

            // Таблица плохих блоков идёт сразу за меткой UPCH; первая пара служебная.
            long bbt = Upch + Main;
            builder.WriteUInt16(bbt + 4, 7);
            builder.WriteUInt16(bbt + 6, 1000);
            builder.WriteUInt16(bbt + 8, 9);
            builder.WriteUInt16(bbt + 10, 1001);
            builder.WriteUInt16(bbt + 14, 0xFFFF);   // конец таблицы

            var (_, detected, context) = RunOnNand(new SamsungNandDetector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(new long[] { 7, 9 }, context.BadBlocks);
        }

        [Fact]
        public void SamsungNand_NeedsBothMarkers()
        {
            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(0x300000, "UPCH");   // LPCH нет

            Assert.False(RunOnNand(new SamsungNandDetector(), builder.Build()).Detected);
        }

        [Fact]
        public void SamsungNand_NeedsNandGeometry()
        {
            // На сплошном дампе eMMC этой разметки быть не может: она вся построена
            // на постраничном переборе.
            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(0x300000, "UPCH");
            builder.WriteAscii(0x320000, "LPCH");

            using var dump = new PlainDumpReader(new MemoryStream(builder.Build()));
            var context = new DumpContext(dump);

            Assert.Null(new SamsungNandDetector().TryDetect(dump, context, new SilentAnalysisHost(), CancellationToken.None));
        }

        private static void AddSamsungRecord(DumpBuilder builder, long start, int index, byte type, ushort offsetBlocks, ushort sizeBlocks)
        {
            long record = start + 0x10 + index * 0x10;
            builder.Write(record, type);
            builder.WriteUInt16(record + 4, offsetBlocks);
            builder.WriteUInt16(record + 6, sizeBlocks);
        }
    }
}
