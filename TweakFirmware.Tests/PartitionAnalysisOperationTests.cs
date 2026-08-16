using System;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Разбор дампа целиком: от файла на диске до готового списка разделов.
    /// Проверяется то, что нельзя проверить на отдельных детекторах, — порядок цепочек,
    /// нормализация и второй проход по файловым системам.
    /// </summary>
    public class PartitionAnalysisOperationTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        public PartitionAnalysisOperationTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* временная папка */ }
        }

        private string WriteDump(byte[] content, string name = "dump.bin")
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private static Task<PartitionAnalysisResult> Run(string path, SilentAnalysisHost host) =>
            PartitionAnalysisOperation.RunAsync(
                new PartitionAnalysisRequest { SourcePath = path }, host, CancellationToken.None);

        [Fact]
        public async Task MissingFileIsReportedNotThrown()
        {
            var result = await Run(Path.Combine(_folder, "no-such-file.bin"), new SilentAnalysisHost());

            Assert.Equal(PartitionAnalysisStatus.SourceNotFound, result.Status);
        }

        [Fact]
        public async Task ReadsAnMbrDumpEndToEnd()
        {
            var builder = new DumpBuilder(0x400000);
            WriteMbrRecord(builder, 0, 0x83, startSector: 2, sectorCount: 100);
            WriteMbrRecord(builder, 1, 0x83, startSector: 1000, sectorCount: 200);
            builder.WriteUInt16(0x1FE, 0xAA55);

            string path = WriteDump(builder.Build());
            var host = new SilentAnalysisHost(confirmAnswer: false);

            var result = await Run(path, host);

            Assert.Equal(PartitionAnalysisStatus.Completed, result.Status);
            Assert.Equal("MBR", result.MarkName);
            Assert.Null(result.Geometry);

            Assert.Contains(result.Table, p => p.Name == "part_00");
            Assert.Contains(result.Table, p => p.Name == "part_01");

            // После нормализации разделы идут подряд и покрывают дамп целиком.
            long covered = 0;
            foreach (var part in result.Table)
            {
                Assert.Equal(covered, part.Offset);
                covered += part.Length;
            }
            Assert.Equal(result.LogicalSize, covered);
        }

        [Fact]
        public async Task SecondPassFindsFileSystemsInsidePartitions()
        {
            var builder = new DumpBuilder(0x400000);
            WriteMbrRecord(builder, 0, 0x83, startSector: 0x800, sectorCount: 0x800);
            builder.WriteUInt16(0x1FE, 0xAA55);

            // Внутри раздела — том ext4.
            long partition = 0x800 * 0x200;
            builder.WriteUInt16(partition + 0x400 + 0x38, 0xEF53);
            builder.WriteUInt32(partition + 0x400 + 0x04, 0x40);      // блоков
            builder.WriteUInt32(partition + 0x400 + 0x18, 2);         // логарифм размера блока
            builder.WriteUInt32(partition + 0x400 + 0x1C, 2);
            builder.WriteAscii(partition + 0x400 + 0x78, "system");

            string path = WriteDump(builder.Build());

            // На вопрос о поиске файловых систем отвечаем согласием.
            var host = new SilentAnalysisHost(confirmAnswer: true);
            var result = await Run(path, host);

            Assert.Equal(PartitionAnalysisStatus.Completed, result.Status);

            var system = result.Table.Single(p => p.Name == "system");
            Assert.Equal(FsType.Ext4, system.FsType);
            Assert.Equal(partition, system.Offset);
        }

        [Fact]
        public async Task WithoutTheSecondPassFileSystemsStayUnknown()
        {
            var builder = new DumpBuilder(0x400000);
            WriteMbrRecord(builder, 0, 0x83, startSector: 0x800, sectorCount: 0x800);
            builder.WriteUInt16(0x1FE, 0xAA55);

            long partition = 0x800 * 0x200;
            builder.WriteUInt16(partition + 0x400 + 0x38, 0xEF53);
            builder.WriteUInt32(partition + 0x400 + 0x04, 0x40);
            builder.WriteUInt32(partition + 0x400 + 0x18, 2);
            builder.WriteUInt32(partition + 0x400 + 0x1C, 2);

            string path = WriteDump(builder.Build());
            var result = await Run(path, new SilentAnalysisHost(confirmAnswer: false));

            Assert.All(result.Table, p => Assert.Equal(FsType.Unknown, p.FsType));
        }

        [Fact]
        public async Task UnrecognisedDumpIsReportedWithoutPartitions()
        {
            string path = WriteDump(new DumpBuilder(0x400000).Pattern(0, 0x1000).Build());

            var result = await Run(path, new SilentAnalysisHost());

            Assert.Equal(PartitionAnalysisStatus.LayoutNotRecognised, result.Status);
            Assert.Empty(result.Table.Items);
        }

        [Fact]
        public async Task RecognisesTheEepromInsteadOfLookingForPartitions()
        {
            var builder = new DumpBuilder(0x2000);
            builder.WriteAscii(0x20, "FUSIO");
            builder.WriteAscii(0x10F8, "55PFL6158K/12");
            builder.WriteAscii(0x1106, "ZH1H1335007420");

            string path = WriteDump(builder.Build(), "eeprom.bin");
            var result = await Run(path, new SilentAnalysisHost());

            Assert.Equal(PartitionAnalysisStatus.EepromRecognised, result.Status);
            Assert.Equal("55PFL6158K/12", result.Eeprom!.Model);
        }

        [Fact]
        public async Task ReadsAPartitionListSavedAsATextFile()
        {
            // Так удобно разбирать список разделов, скопированный из логов загрузки.
            string path = WriteDump(
                Encoding.ASCII.GetBytes("mtdparts=mt53xx-emmc:2M(uboot),4M(kernel),-(userdata)"),
                "parts.txt");

            var result = await Run(path, new SilentAnalysisHost());

            Assert.Equal(PartitionAnalysisStatus.Completed, result.Status);
            Assert.Equal("mtdparts", result.MarkName);

            Assert.Equal("uboot", result.Table[0].Name);
            Assert.Equal(2 * 1024 * 1024, result.Table[0].Length);
            Assert.Equal("userdata", result.Table[2].Name);
            Assert.False(result.Table[2].HasOpenEnd);
        }

        [Fact]
        public async Task PartitionListCanBeCorrectedForNandOnRequest()
        {
            string path = WriteDump(
                Encoding.ASCII.GetBytes("mtdparts=nand:2M(uboot),4M(kernel)"),
                "parts.txt");

            var geometry = new NandGeometry(2048, 64, 0x20, 0x01);
            var result = await PartitionAnalysisOperation.RunAsync(
                new PartitionAnalysisRequest { SourcePath = path, ManualGeometry = geometry },
                new SilentAnalysisHost(),
                CancellationToken.None);

            Assert.Equal(PartitionAnalysisStatus.Completed, result.Status);

            // Размеры пересчитаны с учётом служебных областей.
            Assert.Equal(geometry.AddSpare(2 * 1024 * 1024), result.Table[0].Length);
            Assert.Equal(geometry.AddSpare(2 * 1024 * 1024), result.Table[1].Offset);
        }

        [Fact]
        public async Task ReadsANandDumpThroughTheSecondChain()
        {
            // Дамп NAND с разметкой NVT: она встречается только во второй цепочке.
            var logical = new DumpBuilder(0x1000000);
            logical.WriteAscii(0x700000,
                "mtdparts=nvt_nand:2M(uboot),4M(kernel),8M(rootfs),-(userdata)");

            byte[] physical = DumpBuilder.AsNand(logical.Build(), main: 2048, spare: 64);
            string path = WriteDump(physical);

            var host = new SilentAnalysisHost(confirmAnswer: false);
            var result = await Run(path, host);

            // Дамп меньше порога в 64 МиБ, поэтому геометрия не определяется, и вторая
            // цепочка не запускается — разметку в этом случае найти неоткуда.
            Assert.Equal(PartitionAnalysisStatus.LayoutNotRecognised, result.Status);
        }

        [Fact]
        public async Task CancellationStopsTheAnalysis()
        {
            string path = WriteDump(new DumpBuilder(0x400000).Pattern(0, 0x1000).Build());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await PartitionAnalysisOperation.RunAsync(
                new PartitionAnalysisRequest { SourcePath = path }, new SilentAnalysisHost(), cts.Token);

            Assert.Equal(PartitionAnalysisStatus.Cancelled, result.Status);
        }

        [Fact]
        public void ChainsAreOrderedByPriority()
        {
            // Порядок опроса — часть алгоритма: узкие признаки проверяются раньше широких.
            foreach (var chain in new[]
                     {
                         PartitionAnalysisOperation.CreateCommonChain(),
                         PartitionAnalysisOperation.CreateNandChain()
                     })
            {
                for (int i = 1; i < chain.Count; i++)
                    Assert.True(chain[i].Priority > chain[i - 1].Priority,
                        $"{chain[i - 1].Name} → {chain[i].Name}");
            }
        }

        [Fact]
        public void ChainsFollowTheOrderOfChecksInTheOriginalScript()
        {
            // Порядок опроса решает, какая разметка выиграет на дампе, подходящем сразу
            // под несколько. Здесь он записан явно — ровно так, как идут проверки
            // в analysisDump() исходного скрипта.
            Assert.Equal(
                new[]
                {
                    "NOR FLASH", "MSTAR ANDROID", "SONY MTK EMMC", "MBR",
                    "MTK LG", "MTK ANDROID", "MPT AMLOGIC", "ROCKCHIP PX3", "RECEIVER ANDROID"
                },
                PartitionAnalysisOperation.CreateCommonChain().Select(d => d.Name).ToArray());

            Assert.Equal(
                new[]
                {
                    "AMBARELLA", "BLUERAY", "NOVATEK", "NVT NAND", "HISILICON NAND",
                    "DUNE HD", "QFUx.x", "LG NAND", "SAT RECEIVER",
                    "MSTAR NAND", "MEDIATEK NAND", "SAMSUNG NAND"
                },
                PartitionAnalysisOperation.CreateNandChain().Select(d => d.Name).ToArray());
        }

        [Fact]
        public void EveryDetectorHasItsOwnName()
        {
            var names = PartitionAnalysisOperation.CreateCommonChain()
                .Concat(PartitionAnalysisOperation.CreateNandChain())
                .Select(d => d.Name)
                .ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Equal(21, names.Count);
        }

        [Fact]
        public async Task WhenTwoLayoutsMatch_TheEarlierOneInTheChainWins()
        {
            // Дамп, подходящий сразу под две разметки: настоящая таблица MSTAR ANDROID
            // и рядом обычная запись MBR. В цепочке MSTAR стоит раньше MBR, и решает
            // именно это — первое совпадение останавливает перебор.
            var both = new DumpBuilder(0x400000);
            WriteChinaRecord(both, 0, addrSectors: 0x1000, sizeSectors: 0x400, name: "MBOOT");
            WriteChinaRecord(both, 1, addrSectors: 0x1400, sizeSectors: 0x400, name: "MPOOL");
            WriteMbrRecord(both, 0, 0x83, startSector: 2, sectorCount: 100);
            both.WriteUInt16(0x1FE, 0xAA55);

            var result = await Run(WriteDump(both.Build(), "both.bin"), new SilentAnalysisHost());

            Assert.Equal(PartitionAnalysisStatus.Completed, result.Status);
            Assert.Equal("MSTAR ANDROID", result.MarkName);
            Assert.Contains(result.Table, p => p.Name == "MBOOT");
            Assert.DoesNotContain(result.Table, p => p.Name == "part_00");

            // Тот же MBR без таблицы MSTAR выигрывает — значит, дело именно в порядке,
            // а не в том, что MBR здесь почему-то не опознаётся.
            var mbrOnly = new DumpBuilder(0x400000);
            WriteMbrRecord(mbrOnly, 0, 0x83, startSector: 2, sectorCount: 100);
            mbrOnly.WriteUInt16(0x1FE, 0xAA55);

            var second = await Run(WriteDump(mbrOnly.Build(), "mbr.bin"), new SilentAnalysisHost());

            Assert.Equal("MBR", second.MarkName);
        }

        [Fact]
        public async Task PartitionRunningPastTheEndOfTheDumpIsReported()
        {
            // Запись MBR заявляет 8 МиБ в файле на 4 МиБ — так выглядит и обрезанный
            // дамп, и подпорченная таблица. Извлечение прочитает столько, сколько есть,
            // и запишет файл короче заявленного, ничего не сказав; здесь об этом
            // сообщается заранее.
            var builder = new DumpBuilder(0x400000);
            WriteMbrRecord(builder, 0, 0x83, startSector: 2, sectorCount: 0x4000);
            builder.WriteUInt16(0x1FE, 0xAA55);

            var host = new SilentAnalysisHost();
            var result = await Run(WriteDump(builder.Build(), "short.bin"), host);

            Assert.Equal(PartitionAnalysisStatus.Completed, result.Status);

            var issue = Assert.Single(result.Issues, i => i.Kind == PartitionIssueKind.EndsBeyondDump);
            Assert.Equal(0x400, issue.Offset);

            // И то же самое сказано в журнале — иначе замечание увидел бы только код.
            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
        }

        [Fact]
        public async Task CleanDumpProducesNoNotes()
        {
            // Обратная сторона проверки: на обычном дампе она обязана молчать, иначе
            // предупреждения перестанут читать.
            var builder = new DumpBuilder(0x400000);
            WriteMbrRecord(builder, 0, 0x83, startSector: 2, sectorCount: 100);
            WriteMbrRecord(builder, 1, 0x83, startSector: 1000, sectorCount: 200);
            builder.WriteUInt16(0x1FE, 0xAA55);

            var result = await Run(WriteDump(builder.Build(), "clean.bin"), new SilentAnalysisHost());

            Assert.Empty(result.Issues);
        }

        private static void WriteChinaRecord(DumpBuilder builder, int index, uint addrSectors, uint sizeSectors, string name)
        {
            long record = 0x200 + index * 0x200;
            builder.WriteUInt16(record, 0x5840);
            builder.WriteUInt32(record + 0x08, addrSectors);
            builder.WriteUInt32(record + 0x0C, sizeSectors);
            builder.WriteAscii(record + 0x10, name);
        }

        private static void WriteMbrRecord(DumpBuilder builder, int index, byte type, uint startSector, uint sectorCount)
        {
            long record = 0x1BE + index * 16;
            builder.Write(record + 0x04, type);
            builder.WriteUInt32(record + 0x08, startSector);
            builder.WriteUInt32(record + 0x0C, sectorCount);
        }
    }
}
