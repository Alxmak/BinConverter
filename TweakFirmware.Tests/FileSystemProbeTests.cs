using System;
using System.IO;
using System.Text;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Зонды файловых систем. У каждого проверяется одно и то же: находит настоящий том,
    /// не находит ничего в случайных данных.
    /// </summary>
    public class FileSystemProbeTests
    {
        private static IDumpReader DumpOf(DumpBuilder builder) =>
            new PlainDumpReader(new MemoryStream(builder.Build()));

        // ---------- EXT4 ----------

        /// <summary>Кладёт суперблок ext4 так, как он лежит в настоящем томе.</summary>
        private static DumpBuilder WithExt4(DumpBuilder builder, long offset, uint blockCount, uint logBlockSize = 2, string label = "")
        {
            long sb = offset + 0x400;

            builder.WriteUInt16(sb + 0x38, 0xEF53);
            builder.WriteUInt32(sb + 0x04, blockCount);
            builder.WriteUInt32(sb + 0x18, logBlockSize);
            builder.WriteUInt32(sb + 0x1C, logBlockSize);      // размер фрагмента совпадает
            builder.WriteUInt32(sb + 0x00, 1024);              // всего инод
            builder.WriteUInt32(sb + 0x10, 512);               // свободных инод
            builder.WriteUInt16(sb + 0x3A, 1);                 // размонтирована правильно

            if (label.Length > 0) builder.WriteAscii(sb + 0x78, label);
            return builder;
        }

        [Fact]
        public void Ext4_ReadsSizeAndLabel()
        {
            var builder = WithExt4(new DumpBuilder(0x200000), 0, blockCount: 0x40, label: "system");

            using var dump = DumpOf(builder);
            var volume = new Ext4Probe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(FsType.Ext4, volume!.Type);
            Assert.Equal("system", volume.Name);
            Assert.Equal(0x40 * 4096, volume.Length);
        }

        [Fact]
        public void Ext4_TakesTheNameFromTheMountPathWhenThereIsNoLabel()
        {
            var builder = new DumpBuilder(0x200000);
            WithExt4(builder, 0, blockCount: 0x10);
            builder.WriteAscii(0x400 + 0x88, "/vendor");

            using var dump = DumpOf(builder);
            var volume = new Ext4Probe().TryProbe(dump, 0, 0x200000);

            Assert.Equal("vendor", volume!.Name);
        }

        [Fact]
        public void Ext4_ReservedAreaMustBeBlank()
        {
            // Первый килобайт тома обнулён — на этом держится отсев ложных срабатываний.
            var builder = WithExt4(new DumpBuilder(0x200000), 0, blockCount: 0x10);
            builder.Pattern(0x100, 64);

            using var dump = DumpOf(builder);
            Assert.Null(new Ext4Probe().TryProbe(dump, 0, 0x200000));
        }

        [Fact]
        public void Ext4_BlockAndFragmentSizeMustAgree()
        {
            var builder = WithExt4(new DumpBuilder(0x200000), 0, blockCount: 0x10);
            builder.WriteUInt32(0x400 + 0x1C, 3);   // размер фрагмента другой

            using var dump = DumpOf(builder);
            Assert.Null(new Ext4Probe().TryProbe(dump, 0, 0x200000));
        }

        [Fact]
        public void Ext4_VolumeIsTrimmedToThePartitionBoundary()
        {
            // Заголовок обещает больше, чем занимает раздел, — так бывает у повреждённых
            // прошивок, и доверять заголовку в этом случае нельзя.
            var builder = WithExt4(new DumpBuilder(0x200000), 0, blockCount: 0x1000);

            using var dump = DumpOf(builder);
            var volume = new Ext4Probe().TryProbe(dump, 0, 0x100000);

            Assert.Equal(0x100000, volume!.Length);
            Assert.NotEqual("", volume.Comment);
        }

        // ---------- SQUASHFS ----------

        [Fact]
        public void SquashFs_ReadsVersionFourWithCompression()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0, 0x73717368);
            builder.WriteUInt16(28, 4);
            builder.WriteUInt16(30, 0);
            builder.WriteUInt32(12, 131072);          // размер блока
            builder.WriteUInt16(20, 4);               // сжатие XZ
            builder.WriteUInt16(22, 17);              // логарифм размера блока
            builder.WriteUInt64(40, 0x12345);         // использовано байт

            using var dump = DumpOf(builder);
            var volume = new SquashFsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(FsType.SquashFs, volume!.Type);
            Assert.Equal(0x13000, volume.Length);     // округлено вверх до 4 кБ
            Assert.Equal("", volume.Comment);
            Assert.Contains(volume.Details, d => d.Contains("XZ"));
        }

        [Fact]
        public void SquashFs_RecognisesTheByteSwappedImage()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0, 0x68737173);       // перевёрнутая сигнатура

            // Остальные поля тоже перевёрнуты.
            builder.Write(28, 0x00, 0x04);            // старшая версия 4
            builder.Write(30, 0x00, 0x00);
            builder.Write(12, 0x00, 0x02, 0x00, 0x00);  // 131072
            builder.Write(22, 0x00, 0x11);              // логарифм 17
            builder.Write(40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x23, 0x45);

            using var dump = DumpOf(builder);
            var volume = new SquashFsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(0x13000, volume!.Length);
        }

        [Fact]
        public void SquashFs_MismatchedBlockLogIsReportedButTheVolumeIsKept()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0, 0x73717368);
            builder.WriteUInt16(28, 4);
            builder.WriteUInt32(12, 131072);
            builder.WriteUInt16(22, 3);               // логарифм не тот
            builder.WriteUInt64(40, 0x1000);

            using var dump = DumpOf(builder);
            var volume = new SquashFsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.NotEqual("", volume!.Comment);
        }

        // ---------- ROMFS и CRAMFS ----------

        [Fact]
        public void RomFs_ReadsTheBigEndianHeader()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteAscii(0, "-rom1fs-");
            builder.Write(8, 0x00, 0x01, 0x00, 0x00);   // размер 0x10000, обратный порядок
            builder.WriteAscii(16, "rootfs");

            using var dump = DumpOf(builder);
            var volume = new RomFsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal("rootfs", volume!.Name);
            Assert.Equal(0x10000, volume.Length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0x800)]
        public void CramFs_FindsTheSuperblockInBothPositions(int superblockAt)
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(superblockAt, 0x28CD3D45);
            builder.WriteUInt32(superblockAt + 4, 0x8000);
            builder.WriteAscii(superblockAt + 48, "cramfs_vol");

            using var dump = DumpOf(builder);
            var volume = new CramFsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal("cramfs_vol", volume!.Name);
            Assert.Equal(superblockAt, volume.Offset);
            Assert.Equal(0x8000, volume.Length);
        }

        // ---------- VDFS ----------

        [Fact]
        public void Vdfs_ReadsTheLaterLayout()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0, 0x53464456);
            builder.WriteUInt32(0x200, 0x53464456);
            builder.WriteUInt32(0x204, 0x37303030);      // версия «0007»
            builder.Write(0x2A4, 12);                    // логарифм размера блока: 4096
            builder.WriteUInt64(0x208, 0x100);
            builder.WriteAscii(0x22C, "rootfs");
            builder.WriteAscii(0x23C, "vdfs4");

            using var dump = DumpOf(builder);
            var volume = new VdfsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(FsType.Vdfs, volume!.Type);
            Assert.Equal(0x100 * 4096, volume.Length);
            Assert.StartsWith("rootfs.vdfs", volume.Name);
        }

        [Fact]
        public void Vdfs_RecognisesTheEmmcFsPredecessor()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt64(0, 0x015346434D4D6531);
            builder.WriteUInt64(0x200, 0x015346434D4D6531);
            builder.Write(0x208, 12);                    // логарифм блока: 4096
            builder.Write(0x209, 17);                    // логарифм LEB: 131072
            builder.WriteUInt64(0x20A, 4);
            builder.WriteAscii(0x236, "emmcfs_vol");

            using var dump = DumpOf(builder);
            var volume = new VdfsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal("emmcfs_vol", volume!.Name);

            // Размер считается по LEB, а не по блоку: иначе вышло бы 4 * 4096.
            Assert.Equal(4 * 131072L, volume.Length);
        }

        [Fact]
        public void Vdfs_AppendsTheLayoutVersionAsTheTextItIs()
        {
            // Байты версии — печатные символы «0001»; оригинал приписывал к имени тома
            // само число, хотя в комментарии у проверки указана именно строка.
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0, 0x53464456);
            builder.WriteUInt32(0x200, 0x53464456);
            builder.WriteAscii(0x204, "0001");
            builder.Write(0x209, 12);                    // логарифм размера блока: 4096
            builder.Write(0x20A, 17);                    // логарифм LEB: 131072
            builder.WriteUInt64(0x20B, 8);
            builder.WriteAscii(0x237, "rootfs");

            using var dump = DumpOf(builder);
            var volume = new VdfsProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal("rootfs.vdfs0001", volume!.Name);
            Assert.Equal(8 * 131072L, volume.Length);
        }

        [Fact]
        public void Vdfs_NeedsBothSignatures()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0, 0x53464456);           // вторая сигнатура отсутствует

            using var dump = DumpOf(builder);
            Assert.Null(new VdfsProbe().TryProbe(dump, 0, 0x200000));
        }

        // ---------- TAR ----------

        [Fact]
        public void Tar_WalksTheArchiveByHeaders()
        {
            var builder = new DumpBuilder(0x200000);

            WriteTarHeader(builder, 0, "first.txt", size: 600);      // займёт два блока данных
            WriteTarHeader(builder, 512 * 3, "second.txt", size: 100);

            using var dump = DumpOf(builder);
            var volume = new TarProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(FsType.Tar, volume!.Type);
            Assert.Equal("first.txt", volume.Name);

            // Два заголовка, два блока данных у первого файла и один у второго.
            Assert.Equal(512 * 5, volume.Length);
        }

        [Fact]
        public void Tar_ASingleHeaderIsStillAnArchive()
        {
            // Архив из одного пустого файла — это ровно один блок. Контрольная сумма
            // заголовка случайно не сходится, так что отбрасывать такой случай незачем.
            var builder = new DumpBuilder(0x200000);
            WriteTarHeader(builder, 0, "empty.txt", size: 0);

            using var dump = DumpOf(builder);
            var volume = new TarProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(512, volume!.Length);
        }

        [Fact]
        public void Tar_RandomDataIsNotAnArchive()
        {
            var builder = new DumpBuilder(0x200000).Pattern(0, 0x1000);

            using var dump = DumpOf(builder);
            Assert.Null(new TarProbe().TryProbe(dump, 0, 0x200000));
        }

        private static void WriteTarHeader(DumpBuilder builder, long at, string name, long size)
        {
            var header = new byte[512];

            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            WriteOctal(header, 100, 8, 0x1A4);        // права
            WriteOctal(header, 108, 8, 0);            // владелец
            WriteOctal(header, 116, 8, 0);            // группа
            WriteOctal(header, 124, 12, size);
            WriteOctal(header, 136, 12, 0);           // время
            header[0x9C] = (byte)'0';                 // обычный файл

            // Поле суммы при подсчёте считается заполненным пробелами.
            for (int i = 0; i < 8; i++) header[148 + i] = 0x20;

            uint sum = 0;
            foreach (byte b in header) sum += b;
            WriteOctal(header, 148, 6, sum);
            header[154] = 0;
            header[155] = 0x20;

            builder.Write(at, header);
        }

        private static void WriteOctal(byte[] buffer, int at, int length, long value)
        {
            string text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
            Encoding.ASCII.GetBytes(text).CopyTo(buffer, at);
            buffer[at + length - 1] = 0;
        }

        // ---------- FAT ----------

        [Fact]
        public void Fat16_ReadsLabelAndSize()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt16(0x0B, 512);
            builder.Write(0x0D, 4);
            builder.WriteUInt16(0x0E, 1);
            builder.Write(0x10, 2);
            builder.WriteUInt16(0x11, 512);
            builder.WriteUInt16(0x13, 2048);
            builder.WriteUInt16(0x16, 4);
            builder.WriteAscii(0x2B, "CONFIG     ");
            builder.WriteAscii(0x36, "FAT16   ");
            builder.WriteUInt16(0x1FE, 0xAA55);

            using var dump = DumpOf(builder);
            var volume = new FatVolumeProbe().TryProbe(dump, 0, 0x200000);

            Assert.NotNull(volume);
            Assert.Equal(FsType.Fat16, volume!.Type);
            Assert.Equal("CONFIG", volume.Name);
            Assert.Equal(2048 * 512, volume.Length);
        }

        [Fact]
        public void Fat_UnnamedVolumeGetsTheUsualPlaceholder()
        {
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt16(0x0B, 512);
            builder.Write(0x0D, 4);
            builder.WriteUInt16(0x0E, 1);
            builder.Write(0x10, 2);
            builder.WriteUInt16(0x11, 512);
            builder.WriteUInt16(0x13, 2048);
            builder.WriteUInt16(0x16, 4);
            builder.WriteAscii(0x36, "FAT16   ");
            builder.WriteUInt16(0x1FE, 0xAA55);

            using var dump = DumpOf(builder);
            Assert.Equal("NO_NAME", new FatVolumeProbe().TryProbe(dump, 0, 0x200000)!.Name);
        }

        // ---------- сканер ----------

        [Fact]
        public void Scanner_FindsSeveralVolumesInOneRegion()
        {
            var builder = new DumpBuilder(0x200000);

            WithExt4(builder, 0x1000, blockCount: 0x10, label: "system");

            builder.WriteUInt32(0x40000, 0x73717368);
            builder.WriteUInt16(0x40000 + 28, 4);
            builder.WriteUInt32(0x40000 + 12, 131072);
            builder.WriteUInt16(0x40000 + 22, 17);
            builder.WriteUInt64(0x40000 + 40, 0x8000);

            using var dump = DumpOf(builder);
            var table = new PartitionTable();

            int found = FileSystemScanner.Scan(dump, 0, 0x200000, table, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(2, found);
            Assert.Equal("system", table[0].Name);
            Assert.Equal(FsType.Ext4, table[0].FsType);
            Assert.Equal(FsType.SquashFs, table[1].FsType);
            Assert.Equal(0x40000, table[1].Offset);
        }

        [Fact]
        public void ScanPartitions_ReplacesOnlyThePartitionsWhereSomethingWasFound()
        {
            var builder = new DumpBuilder(0x200000);
            WithExt4(builder, 0x100000, blockCount: 0x10, label: "userdata");

            using var dump = DumpOf(builder);

            var table = new PartitionTable();
            table.Add("MBR", 0, 0x200);
            table.Add("empty_part", 0x1000, 0x1000);
            table.Add("data", 0x100000, 0x100000);

            FileSystemScanner.ScanPartitions(dump, table, new SilentAnalysisHost(), CancellationToken.None);

            // Запись разметки и раздел без файловой системы остались как были.
            Assert.Equal("MBR", table[0].Name);
            Assert.Equal("empty_part", table[1].Name);

            // А раздел с томом заменён найденным томом.
            Assert.Equal("userdata", table[2].Name);
            Assert.Equal(FsType.Ext4, table[2].FsType);
        }

        [Fact]
        public void ScanPartitions_UnnamedVolumeInheritsThePartitionName()
        {
            // У SquashFS имени в заголовке нет вовсе, а имя раздела осмысленное.
            var builder = new DumpBuilder(0x200000);
            builder.WriteUInt32(0x1000, 0x73717368);
            builder.WriteUInt16(0x1000 + 28, 4);
            builder.WriteUInt32(0x1000 + 12, 131072);
            builder.WriteUInt16(0x1000 + 22, 17);
            builder.WriteUInt64(0x1000 + 40, 0x2000);

            using var dump = DumpOf(builder);

            var table = new PartitionTable();
            table.Add("rootfsA", 0x1000, 0x10000);

            FileSystemScanner.ScanPartitions(dump, table, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal("rootfsA", table[0].Name);
            Assert.Equal(FsType.SquashFs, table[0].FsType);
        }
    }
}
