using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Operations;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Вывод результата разбора наружу: файл .udev, извлечение разделов и разрезание
    /// дампа NAND на две части.
    /// </summary>
    public class PartitionOutputTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        public PartitionOutputTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* временная папка */ }
        }

        // ---------- имена файлов ----------

        [Fact]
        public void FileName_FollowsTheProgrammerConvention()
        {
            // По этой схеме программатор умеет обратно наполнять таблицу разделов
            // кнопкой «Загрузить из папки» — менять её нельзя.
            string name = PartitionFileNaming.ForPartition(0x30C0000, 0x2BC00000, "system", FsType.Ext4);

            Assert.Equal("USER_0x00030C0000_0x2BC00000_system.ext4.bin", name);
        }

        [Fact]
        public void FileName_WithoutAKnownFileSystemHasNoExtraExtension()
        {
            Assert.Equal("USER_0x0000000000_0x00200000_uboot.bin",
                PartitionFileNaming.ForPartition(0, 0x200000, "uboot", FsType.Unknown));
        }

        [Fact]
        public void FileName_ReplacesCharactersTheFileSystemWouldReject()
        {
            string name = PartitionFileNaming.ForPartition(0, 0x1000, "part/with:bad*chars", FsType.Unknown);

            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain(':', name);
            Assert.DoesNotContain('*', name);
        }

        // ---------- .udev ----------

        private static PartitionTable SampleTable()
        {
            var table = new PartitionTable();
            table.Add("Partitions_table", 0, 0x200000);
            table.Add("system", 0x30C0000, 0x2BC00000, FsType.Ext4);
            table.Add("userdata", 0x2ECC0000, 0x40400000, FsType.Fat32);
            return table;
        }

        [Fact]
        public void Udev_EmmcFileMatchesTheDocumentedFormat()
        {
            var writer = new StringWriter();
            UdevWriter.Write(writer, SampleTable());

            string text = writer.ToString();

            Assert.Contains("[DESC]", text);
            Assert.Contains("Name = Partitions", text);
            Assert.Contains("FlashType1 = eMMC", text);
            Assert.Contains("[PARTITIONS]", text);
            Assert.Contains("PartitionsMode = true", text);

            // Строка раздела: адрес, размер, имя, часть носителя, имя файла, смещение, ФС.
            Assert.Contains(
                "0x00030C0000,0x2BC00000,system,USER,USER_0x00030C0000_0x2BC00000_system.ext4.bin,0,EXT4",
                text);

            // У дампа eMMC флагов о служебных областях быть не должно.
            Assert.DoesNotContain("RawAddrMode", text);
        }

        [Fact]
        public void Udev_NandFileCarriesTheAddressModeFlags()
        {
            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);

            var logical = new StringWriter();
            UdevWriter.Write(logical, SampleTable(), geometry, UdevWriter.AddressMode.Logical);

            var physical = new StringWriter();
            UdevWriter.Write(physical, SampleTable(), geometry, UdevWriter.AddressMode.Physical);

            Assert.Contains("FlashType1 = NAND", logical.ToString());
            Assert.Contains("RawAddrMode = false", logical.ToString());
            Assert.Contains("RawAddrMode = true", physical.ToString());

            // В физическом варианте адреса сдвинуты на служебные области.
            Assert.Contains($"0x{geometry.AddSpare(0x30C0000):X10}", physical.ToString());
        }

        [Fact]
        public void Udev_CommaInANameWouldBreakTheLineSoItIsReplaced()
        {
            var table = new PartitionTable();
            table.Add("bad,name", 0, 0x1000);

            var writer = new StringWriter();
            UdevWriter.Write(writer, table);

            string line = writer.ToString().Split('\n').First(l => l.StartsWith("0x"));
            Assert.Equal(7, line.Split(',').Length);
        }

        // ---------- извлечение ----------

        private string WriteDump(byte[] content, string name = "dump.bin")
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        [Fact]
        public async Task Extract_WritesSelectedPartitionsOnly()
        {
            byte[] content = new DumpBuilder(0x10000).Pattern(0, 0x10000).Build();
            string path = WriteDump(content);

            var table = new PartitionTable();
            table.Add("first", 0, 0x1000);
            table.Add("second", 0x1000, 0x2000).Selected = false;
            table.Add("third", 0x3000, 0x1000);

            var outcome = await PartitionExtractOperation.RunAsync(
                new PartitionExtractRequest { SourcePath = path, CheckDiskSpace = false },
                table, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(PartitionExtractStatus.Completed, outcome.Status);
            Assert.Equal(2, outcome.ExtractedCount);

            var files = Directory.GetFiles(outcome.OutputFolder).Select(Path.GetFileName).ToList();
            Assert.Contains(files, f => f!.Contains("first"));
            Assert.Contains(files, f => f!.Contains("third"));
            Assert.DoesNotContain(files, f => f!.Contains("second"));

            // Содержимое совпадает байт в байт с исходным куском дампа.
            string firstFile = Directory.GetFiles(outcome.OutputFolder).First(f => f.Contains("first"));
            Assert.Equal(content[0..0x1000], File.ReadAllBytes(firstFile));
        }

        [Fact]
        public async Task Extract_FolderSuffixSaysWhatWasExtracted()
        {
            // Оригинал брал суффикс из глобального флага, который к моменту извлечения
            // хранил результат прошлого разбора, и путал папки местами.
            string path = WriteDump(new DumpBuilder(0x2000).Build());

            var table = new PartitionTable();
            table.Add("part", 0, 0x1000);

            var parts = await PartitionExtractOperation.RunAsync(
                new PartitionExtractRequest
                {
                    SourcePath = path,
                    Source = ExtractionSource.PartitionTable,
                    CheckDiskSpace = false
                },
                table, new SilentAnalysisHost(), CancellationToken.None);

            var fileSystems = await PartitionExtractOperation.RunAsync(
                new PartitionExtractRequest
                {
                    SourcePath = path,
                    Source = ExtractionSource.FileSystems,
                    CheckDiskSpace = false
                },
                table, new SilentAnalysisHost(), CancellationToken.None);

            Assert.EndsWith("_parts_", parts.OutputFolder);
            Assert.EndsWith("_fs_", fileSystems.OutputFolder);
        }

        [Fact]
        public async Task Extract_NandDumpGivesBothTheFullAndTheCleanedFile()
        {
            const int Main = 2048, Spare = 64;

            byte[] logical = new DumpBuilder(Main * 8).Pattern(0, Main * 8).Build();
            byte[] physical = DumpBuilder.AsNand(logical, Main, Spare, spareFill: 0xAB);
            string path = WriteDump(physical);

            var geometry = new NandGeometry(Main, Spare, 0x20, 0x01);

            var table = new PartitionTable();
            table.Add("boot", 0, Main * 4);

            var outcome = await PartitionExtractOperation.RunAsync(
                new PartitionExtractRequest { SourcePath = path, Geometry = geometry, CheckDiskSpace = false },
                table, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(PartitionExtractStatus.Completed, outcome.Status);

            // Очищенный файл — ровно полезные байты раздела.
            string cleaned = Directory.GetFiles(outcome.MainOnlyFolder).Single();
            Assert.Equal(logical[0..(Main * 4)], File.ReadAllBytes(cleaned));

            // Полный — те же данные вперемешку со служебными областями.
            string full = Directory.GetFiles(outcome.OutputFolder).Single();
            var fullBytes = File.ReadAllBytes(full);

            Assert.Equal(geometry.AddSpare(Main * 4), fullBytes.Length);
            Assert.Equal(logical[0..Main], fullBytes[0..Main]);
            Assert.All(fullBytes[Main..(Main + Spare)], b => Assert.Equal(0xAB, b));
        }

        [Fact]
        public async Task Extract_NothingSelectedIsReportedNotWritten()
        {
            string path = WriteDump(new DumpBuilder(0x2000).Build());

            var table = new PartitionTable();
            table.Add("part", 0, 0x1000).Selected = false;

            var outcome = await PartitionExtractOperation.RunAsync(
                new PartitionExtractRequest { SourcePath = path, CheckDiskSpace = false },
                table, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(PartitionExtractStatus.NothingSelected, outcome.Status);
        }

        [Fact]
        public async Task Extract_CancellationLeavesNoHalfWrittenFiles()
        {
            // Недописанный файл выглядит как готовый раздел, а данные в нём неполные.
            string path = WriteDump(new DumpBuilder(0x100000).Pattern(0, 0x100000).Build());

            var table = new PartitionTable();
            for (int i = 0; i < 8; i++) table.Add($"part_{i}", i * 0x2000, 0x2000);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await PartitionExtractOperation.RunAsync(
                new PartitionExtractRequest { SourcePath = path, CheckDiskSpace = false },
                table, new SilentAnalysisHost(), cts.Token);

            Assert.Equal(PartitionExtractStatus.Cancelled, outcome.Status);

            string folder = Path.Combine(_folder, "dump_parts_");
            if (Directory.Exists(folder)) Assert.Empty(Directory.GetFiles(folder));
        }

        // ---------- разрезание NAND ----------

        [Fact]
        public async Task Split_WritesMainAndSpareInOnePass()
        {
            const int Main = 2048, Spare = 64;

            byte[] logical = new DumpBuilder(Main * 6).Pattern(0, Main * 6).Build();
            byte[] physical = DumpBuilder.AsNand(logical, Main, Spare, spareFill: 0xCD);
            string path = WriteDump(physical);

            var geometry = new NandGeometry(Main, Spare, 0x20, 0x01);
            string outputFolder = Path.Combine(_folder, "split");

            var outcome = await NandSplitOperation.RunAsync(
                path, geometry, outputFolder, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(NandSplitStatus.Completed, outcome.Status);

            Assert.Equal(logical, File.ReadAllBytes(outcome.MainPath));

            var spare = File.ReadAllBytes(outcome.SparePath);
            Assert.Equal(Spare * 6, spare.Length);
            Assert.All(spare, b => Assert.Equal(0xCD, b));
        }

        [Fact]
        public async Task Split_RebuiltFromMainAndSpareGivesBackTheOriginalDumpByteForByte()
        {
            // Разрезание — единственная операция, которая разбирает дамп на два потока,
            // и ошибка в шаге страницы здесь не видна глазами: файлы получаются нужного
            // размера, а данные в них смещены. Единственная надёжная проверка — собрать
            // обратно и сверить хэш с исходным файлом.
            const int Main = 512, Spare = 16, Pages = 37;

            byte[] logical = new DumpBuilder(Main * Pages).Pattern(0, Main * Pages, seed: 3).Build();

            // Служебные области заполняются по-разному от страницы к странице: с одной
            // и той же заливкой перепутанные местами страницы дали бы тот же файл.
            byte[] physical = new byte[(Main + Spare) * Pages];
            for (int page = 0; page < Pages; page++)
            {
                Array.Copy(logical, page * Main, physical, page * (Main + Spare), Main);
                for (int i = 0; i < Spare; i++)
                    physical[page * (Main + Spare) + Main + i] = (byte)(page * 7 + i);
            }

            string path = WriteDump(physical);
            string outputFolder = Path.Combine(_folder, "roundtrip");

            var outcome = await NandSplitOperation.RunAsync(
                path, new NandGeometry(Main, Spare, 0x20, 0x01), outputFolder,
                new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(NandSplitStatus.Completed, outcome.Status);

            byte[] main = File.ReadAllBytes(outcome.MainPath);
            byte[] spare = File.ReadAllBytes(outcome.SparePath);

            Assert.Equal(Main * Pages, main.Length);
            Assert.Equal(Spare * Pages, spare.Length);

            byte[] rebuilt = new byte[physical.Length];
            for (int page = 0; page < Pages; page++)
            {
                Array.Copy(main, page * Main, rebuilt, page * (Main + Spare), Main);
                Array.Copy(spare, page * Spare, rebuilt, page * (Main + Spare) + Main, Spare);
            }

            Assert.Equal(SHA256.HashData(physical), SHA256.HashData(rebuilt));
        }

        [Fact]
        public async Task Split_RefusesADumpWithoutNandGeometry()
        {
            string path = WriteDump(new DumpBuilder(0x2000).Build());

            var outcome = await NandSplitOperation.RunAsync(
                path, geometry: null, _folder, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(NandSplitStatus.NotNandDump, outcome.Status);
        }

        [Fact]
        public async Task Split_DuneHdLayoutGivesOnlyTheDataFile()
        {
            // У Dune HD служебные байты разбросаны внутри страницы вперемешку с мусором —
            // отдельный файл из них смысла не имеет.
            byte[] logical = new DumpBuilder(2048 * 4).Pattern(0, 2048 * 4).Build();
            string path = WriteDump(DumpBuilder.AsDuneHd(logical));

            var geometry = new NandGeometry(2048, 64, 0x20, 0x01, NandPageLayout.DuneHd, DuneHdVariant.Tv301);

            var outcome = await NandSplitOperation.RunAsync(
                path, geometry, Path.Combine(_folder, "dune"), new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(NandSplitStatus.Completed, outcome.Status);
            Assert.Equal("", outcome.SparePath);
            Assert.True(File.Exists(outcome.MainPath));
        }
    }
}
