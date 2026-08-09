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
    /// Обход каталогов ext4 и чтение build.prop. Том собирается синтетически: суперблок,
    /// один дескриптор группы, таблица инод и блоки с записями каталогов.
    /// </summary>
    public class Ext4DirectoryReaderTests
    {
        private const int BlockSize = 4096;
        private const int InodeSize = 256;
        private const uint InodesPerGroup = 64;

        /// <summary>Номер блока, с которого начинается таблица инод.</summary>
        private const uint InodeTableBlock = 2;

        /// <summary>Блоки, отданные под данные каталогов и файлов.</summary>
        private const uint RootDataBlock = 8;

        private const uint SystemDirBlock = 9;
        private const uint FileDataBlock = 10;

        /// <summary>
        /// Собирает том ext4 с корневым каталогом, каталогом system внутри него и файлом
        /// build.prop заданного содержимого.
        /// </summary>
        private static byte[] BuildVolume(string buildProp, bool propInRoot)
        {
            var builder = new DumpBuilder(BlockSize * 16);

            // Суперблок: он лежит через килобайт от начала тома.
            long sb = 0x400;
            builder.WriteUInt16(sb + 0x38, 0xEF53);
            builder.WriteUInt32(sb + 0x04, 16);          // блоков в томе
            builder.WriteUInt32(sb + 0x18, 2);           // логарифм размера блока: 4096
            builder.WriteUInt32(sb + 0x1C, 2);
            builder.WriteUInt32(sb + 0x28, InodesPerGroup);
            builder.WriteUInt16(sb + 0x58, InodeSize);
            builder.WriteAscii(sb + 0x78, "system");

            // Дескриптор группы: один, в блоке сразу за суперблоком.
            builder.WriteUInt32(BlockSize + 0x08, InodeTableBlock);

            // Инода 2 — корневой каталог.
            WriteInode(builder, inode: 2, firstBlock: RootDataBlock, size: BlockSize);

            uint propInode = propInRoot ? 12u : 13u;

            if (propInRoot)
            {
                WriteDirectory(builder, RootDataBlock,
                    (".", 2, 0x02), ("build.prop", propInode, 0x01));
            }
            else
            {
                WriteDirectory(builder, RootDataBlock,
                    (".", 2, 0x02), ("system", 11, 0x02));

                WriteInode(builder, inode: 11, firstBlock: SystemDirBlock, size: BlockSize);
                WriteDirectory(builder, SystemDirBlock,
                    (".", 11, 0x02), ("build.prop", propInode, 0x01));
            }

            var content = Encoding.ASCII.GetBytes(buildProp);
            WriteInode(builder, propInode, FileDataBlock, content.Length);
            builder.Write(FileDataBlock * BlockSize, content);

            return builder.Build();
        }

        private static void WriteInode(DumpBuilder builder, uint inode, uint firstBlock, long size)
        {
            long at = InodeTableBlock * BlockSize + (long)(inode - 1) * InodeSize;

            builder.WriteUInt32(at + 0x04, (uint)size);
            builder.WriteUInt16(at + 0x28, 0xF30A);      // заголовок экстентов
            builder.WriteUInt32(at + 0x3C, firstBlock);  // первый блок данных
        }

        private static void WriteDirectory(DumpBuilder builder, uint block, params (string Name, uint Inode, byte Type)[] entries)
        {
            long at = block * BlockSize;

            foreach (var (name, inode, type) in entries)
            {
                int nameLength = name.Length;
                int recordLength = (8 + nameLength + 3) / 4 * 4;

                builder.WriteUInt32(at, inode);
                builder.WriteUInt16(at + 4, (ushort)recordLength);
                builder.Write(at + 6, (byte)nameLength);
                builder.Write(at + 7, type);
                builder.WriteAscii(at + 8, name);

                at += recordLength;
            }
        }

        private static Ext4DirectoryReader OpenReader(byte[] volume, out IDumpReader dump)
        {
            dump = new PlainDumpReader(new MemoryStream(volume));

            var superblock = Ext4Probe.TryReadSuperblock(dump, 0);
            Assert.NotNull(superblock);

            var reader = Ext4DirectoryReader.Open(dump, superblock!);
            Assert.NotNull(reader);
            return reader!;
        }

        [Fact]
        public void ReadsTheRootDirectory()
        {
            var volume = BuildVolume("ro.product.model=TV\n", propInRoot: true);
            var reader = OpenReader(volume, out var dump);

            using (dump)
            {
                var root = reader.ReadRoot();

                Assert.Contains(root, e => e.Name == "build.prop" && e.IsFile);
                Assert.NotNull(reader.Find(root, "build.prop"));
                Assert.Null(reader.Find(root, "no-such-file"));
            }
        }

        [Fact]
        public void ReadsAFileThroughItsFirstExtent()
        {
            const string Content = "ro.product.model=TV\nro.build.version.release=9\n";

            var volume = BuildVolume(Content, propInRoot: true);
            var reader = OpenReader(volume, out var dump);

            using (dump)
            {
                var file = reader.Find(reader.ReadRoot(), "build.prop");
                var bytes = reader.ReadFile(file!.Inode);

                Assert.NotNull(bytes);
                Assert.Equal(Content, Encoding.ASCII.GetString(bytes!));
            }
        }

        [Fact]
        public void DescendsIntoTheSystemDirectory()
        {
            var volume = BuildVolume("ro.product.model=TV\n", propInRoot: false);
            var reader = OpenReader(volume, out var dump);

            using (dump)
            {
                var root = reader.ReadRoot();
                var systemDir = reader.Find(root, "system");

                Assert.NotNull(systemDir);
                Assert.True(systemDir!.IsDirectory);

                var inside = reader.ReadDirectory(systemDir.Inode);
                Assert.NotNull(reader.Find(inside, "build.prop"));
            }
        }

        // ---------- разбор build.prop ----------

        [Fact]
        public void ParsesTheBuildFingerprint()
        {
            const string Prop =
                "ro.build.fingerprint=Sony/BRAVIA_ATV3_EU/BRAVIA_ATV3:9/PTT1.190515.001/1234:user/release-keys\n" +
                "ro.product.manufacturer=Sony\n" +
                "ro.board.platform=mt5891\n";

            var info = AndroidInfoReader.Parse(Encoding.ASCII.GetBytes(Prop));

            Assert.Equal("Sony", info.Brand);
            Assert.Equal("BRAVIA_ATV3_EU", info.Name);
            Assert.Equal("BRAVIA_ATV3", info.Device);
            Assert.Equal("9", info.Release);
            Assert.Equal("Sony", info.Manufacturer);
            Assert.Equal("mt5891", info.Platform);
        }

        [Fact]
        public void FallsBackToIndividualPropertiesWhenThereIsNoFingerprint()
        {
            const string Prop =
                "ro.product.brand=Philips\n" +
                "ro.product.name=PH5000\n" +
                "ro.product.device=ph_tv\n" +
                "ro.build.version.release=11\n" +
                "ro.product.model=50PUS8506\n" +
                "ro.tv.swversion=TPM191E\n";

            var info = AndroidInfoReader.Parse(Encoding.ASCII.GetBytes(Prop));

            Assert.Equal("Philips", info.Brand);
            Assert.Equal("PH5000", info.Name);
            Assert.Equal("ph_tv", info.Device);
            Assert.Equal("11", info.Release);
            Assert.Equal("50PUS8506", info.Model);
            Assert.Equal("TPM191E", info.SoftwareVersion);
        }

        [Fact]
        public void HandlesWindowsLineEndings()
        {
            var info = AndroidInfoReader.Parse(Encoding.ASCII.GetBytes("ro.product.model=TV-42\r\nro.product.brand=LG\r\n"));

            Assert.Equal("TV-42", info.Model);
            Assert.Equal("LG", info.Brand);
        }

        [Fact]
        public void FindsBuildPropThroughTheWholePipeline()
        {
            var volume = BuildVolume("ro.product.brand=Sony\nro.product.model=KD-43\n", propInRoot: false);

            using var dump = new PlainDumpReader(new MemoryStream(volume));

            var table = new PartitionTable();
            table.Add("system", 0, volume.Length, FsType.Ext4);

            var host = new SilentAnalysisHost();
            var info = AndroidInfoReader.TryRead(dump, table, host, CancellationToken.None);

            Assert.NotNull(info);
            Assert.Equal("Sony", info!.Brand);
            Assert.Equal("KD-43", info.Model);
            Assert.NotEmpty(info.RawBuildProp);
            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Found);
        }

        [Fact]
        public void ReportsWhenThereIsNothingToRead()
        {
            using var dump = new PlainDumpReader(new MemoryStream(new DumpBuilder(0x10000).Build()));

            var table = new PartitionTable();
            table.Add("data", 0, 0x10000, FsType.Ext4);

            var host = new SilentAnalysisHost();

            Assert.Null(AndroidInfoReader.TryRead(dump, table, host, CancellationToken.None));
            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
        }
    }
}
