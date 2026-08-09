using System;
using System.IO;
using System.Text;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Разметки, описанные строкой параметров ядра. Их восемь, и различаются они только
    /// тем, где искать блок конфигурации и как убедиться, что это он, — поэтому проверки
    /// здесь однотипные: находит нужное, не находит лишнего.
    /// </summary>
    public class MtdPartsDetectorTests
    {
        private const string PartsString = "mtdparts=mt53xx-emmc:2M(uboot),4M(kernel),8M(rootfs),-(userdata)";

        /// <summary>
        /// Собирает блок конфигурации: строка внутри, а первые четыре байта — контрольная
        /// сумма всего остального, как в настоящих дампах.
        /// </summary>
        private static void PutConfigBlock(DumpBuilder builder, long offset, int blockLength, string text,
            int checksummedLength = 0, bool withCrc = true, string prefix = "")
        {
            var block = new byte[blockLength];

            // Немного текста «ключ=значение» перед списком: по нему MSTAR узнаёт конфигурацию.
            Encoding.ASCII.GetBytes(prefix).CopyTo(block, 4);
            Encoding.ASCII.GetBytes(text).CopyTo(block, 0x100);

            if (withCrc)
            {
                int length = checksummedLength == 0 ? blockLength : checksummedLength;
                uint crc = Crc32.Compute(block.AsSpan(4, length - 4));
                for (int i = 0; i < 4; i++) block[i] = (byte)(crc >> (i * 8));
            }

            builder.Write(offset, block);
        }

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

        private static void AssertStandardParts(PartitionTable table)
        {
            Assert.Equal(4, table.Count);
            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(2 * 1024 * 1024, table[0].Length);
            Assert.Equal("kernel", table[1].Name);
            Assert.Equal(2 * 1024 * 1024, table[1].Offset);
            Assert.Equal("userdata", table[3].Name);
            Assert.True(table[3].HasOpenEnd);
        }

        [Theory]
        [InlineData(0x00200000)]
        [InlineData(0x00480000)]
        [InlineData(0x00900000)]
        [InlineData(0x00400000)]
        public void MtkAndroid_FindsTheBlockAtEveryKnownAddress(long offset)
        {
            var builder = new DumpBuilder(0x2000000);
            PutConfigBlock(builder, offset, 0x20000, PartsString);

            var (table, detected) = Run(new MtkAndroidDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void MtkAndroid_IgnoresABlockWithABrokenChecksum()
        {
            // Строка на месте, сумма не сходится — значит, это случайное совпадение.
            var builder = new DumpBuilder(0x2000000);
            PutConfigBlock(builder, 0x200000, 0x20000, PartsString, withCrc: false);

            Assert.False(Run(new MtkAndroidDetector(), builder.Build()).Detected);
        }

        [Fact]
        public void ReceiverAndroid_ReadsBlockDeviceParts()
        {
            var builder = new DumpBuilder(0x400000);
            PutConfigBlock(builder, 0x100000, 0x10000,
                "blkdevparts=mmcblk0:2M(uboot),4M(kernel),8M(rootfs),-(userdata)");

            var (table, detected) = Run(new ReceiverAndroidDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void NorFlash_ScansTheWholeChipButOnlyAtKnownSizes()
        {
            var builder = new DumpBuilder(0x800000);
            PutConfigBlock(builder, 0x30000, 0x10000,
                "mtdparts=nor-flash:2M(uboot),4M(kernel),8M(rootfs),-(userdata)");

            var (table, detected) = Run(new NorFlashDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void NorFlash_SkipsDumpsOfOtherSizes()
        {
            // Микросхемы NOR бывают на 4, 8 и 16 МиБ; всё остальное — не к этому детектору.
            var builder = new DumpBuilder(0x300000);
            PutConfigBlock(builder, 0x10000, 0x10000,
                "mtdparts=nor-flash:2M(uboot),4M(kernel),8M(rootfs),-(userdata)");

            Assert.False(Run(new NorFlashDetector(), builder.Build()).Detected);
        }

        [Fact]
        public void RockchipPx3_ConvertsSectorsToBytes()
        {
            // У этой разметки размеры в строке заданы в секторах по 512 байт.
            var builder = new DumpBuilder(0x1000000);
            var block = new byte[0x100000];
            Encoding.ASCII.GetBytes("PARM").CopyTo(block, 0);
            Encoding.ASCII.GetBytes("mtdparts=rk29xxnand:0x00002000@0x00002000(uboot),0x00004000@0x00004000(kernel),-@0x00008000(userdata)")
                .CopyTo(block, 0x100);
            builder.Write(0x500000, block);

            var (table, detected) = Run(new RockchipPx3Detector(), builder.Build());

            Assert.True(detected);
            Assert.Equal(0x2000 * 0x200L, table[0].Offset);
            Assert.Equal(0x2000 * 0x200L, table[0].Length);
            Assert.Equal(0x8000 * 0x200L, table[2].Offset);

            // Открытый конец пересчёту не подлежит: настоящий размер подставится позже.
            Assert.True(table[2].HasOpenEnd);
        }

        [Fact]
        public void HiSiliconNand_ChecksOnlyTheFirstFourKilobytes()
        {
            var builder = new DumpBuilder(0x400000);
            PutConfigBlock(builder, 0x80000, 0x20000,
                "mtdparts=hinand:2M(uboot),4M(kernel),8M(rootfs),-(userdata)",
                checksummedLength: 4096);

            var (table, detected) = Run(new HiSiliconNandDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void NvtNand_NeedsNoChecksumAtAll()
        {
            var builder = new DumpBuilder(0x1000000);
            PutConfigBlock(builder, 0x700000, 0x20000,
                "mtdparts=nvt_nand:2M(uboot),4M(kernel),8M(rootfs),-(userdata)",
                withCrc: false);

            var (table, detected) = Run(new NvtNandDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void MediatekNand_RequiresTheBootloaderMarkerFirst()
        {
            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(0x100, "MTK/DTV/ROMCODE/NANDBOOT/V1.0");
            builder.WriteAscii(0x200000 + 0x40,
                "mtdparts=mt53xx-nand:2M(uboot),4M(kernel),8M(rootfs),-(userdata)");

            var (table, detected) = Run(new MediatekNandDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void MediatekNand_WithoutTheMarkerLooksNoFurther()
        {
            var builder = new DumpBuilder(0x400000);
            builder.WriteAscii(0x200000 + 0x40,
                "mtdparts=mt53xx-nand:2M(uboot),4M(kernel),8M(rootfs),-(userdata)");

            Assert.False(Run(new MediatekNandDetector(), builder.Build()).Detected);
        }

        [Fact]
        public void MstarNand_FindsTheConfigBlockByScanning()
        {
            // Блок лежит не по круглому адресу — его находят перебором.
            var builder = new DumpBuilder(0x1000000);
            PutConfigBlock(builder, 0x333000, 0x20000,
                "mtdparts=edb64M-nand:2M(uboot),4M(kernel),8M(rootfs),-(userdata)",
                checksummedLength: 65536,
                prefix: "a=1 b=2 c=3 d=4 ");

            var (table, detected) = Run(new MstarNandDetector(), builder.Build());

            Assert.True(detected);
            AssertStandardParts(table);
        }

        [Fact]
        public void MstarNand_KeyValueTextAloneIsNotEnough()
        {
            // Знаков равенства достаточно, но контрольная сумма не сходится.
            var builder = new DumpBuilder(0x1000000);
            PutConfigBlock(builder, 0x333000, 0x20000,
                "mtdparts=edb64M-nand:2M(uboot),4M(kernel)",
                withCrc: false,
                prefix: "a=1 b=2 c=3 d=4 ");

            Assert.False(Run(new MstarNandDetector(), builder.Build()).Detected);
        }

        [Fact]
        public void MstarNand_StopsWhenCancelled()
        {
            var builder = new DumpBuilder(0x1000000);
            using var dump = new PlainDumpReader(new MemoryStream(builder.Build()));
            var context = new DumpContext(dump);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                new MstarNandDetector().TryDetect(dump, context, new SilentAnalysisHost(), cts.Token));
        }
    }
}
