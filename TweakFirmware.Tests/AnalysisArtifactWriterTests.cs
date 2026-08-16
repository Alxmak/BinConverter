using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Operations;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Побочные находки разбора: build.prop из прошивки Android и файл лицензии Dune HD.
    /// В оригинале оба писались на диск прямо во время разбора; здесь разбор только
    /// находит их, а записывает отдельный шаг.
    /// </summary>
    public class AnalysisArtifactWriterTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        public AnalysisArtifactWriterTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* временная папка */ }
        }

        private string WriteDump(byte[] content)
        {
            string path = Path.Combine(_folder, "dump.bin");
            File.WriteAllBytes(path, content);
            return path;
        }

        [Fact]
        public async Task SavesBuildPropNextToTheDump()
        {
            const string Content = "ro.product.brand=Sony\nro.product.model=KD-43\n";

            string path = WriteDump(new DumpBuilder(0x1000).Build());

            var result = new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                Android = new AndroidInfo { Brand = "Sony", RawBuildProp = Encoding.ASCII.GetBytes(Content) }
            };

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                result, path, "", new SilentAnalysisHost(), CancellationToken.None);

            string expected = Path.Combine(_folder, AnalysisArtifactWriter.BuildPropFileName);

            Assert.Contains(expected, artifacts.Files);
            Assert.Equal(Content, File.ReadAllText(expected));
        }

        [Fact]
        public async Task SavesTheDuneLicenceReadBackFromTheDump()
        {
            const string Licence = "LICENSE-BODY-1234";

            var builder = new DumpBuilder(0x10000);
            builder.WriteAscii(0x4200, Licence);
            string path = WriteDump(builder.Build());

            var result = new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                DuneLicense = new DuneLicense(0x4200, Licence.Length)
            };

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                result, path, "", new SilentAnalysisHost(), CancellationToken.None);

            string expected = Path.Combine(_folder, AnalysisArtifactWriter.DuneLicenseFileName);

            Assert.Contains(expected, artifacts.Files);
            Assert.Equal(Licence, File.ReadAllText(expected));
        }

        [Fact]
        public async Task WritesIntoTheChosenFolderWhenOneIsGiven()
        {
            string path = WriteDump(new DumpBuilder(0x1000).Build());
            string outputFolder = Path.Combine(_folder, "output");

            var result = new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                Android = new AndroidInfo { RawBuildProp = Encoding.ASCII.GetBytes("ro.product.model=TV\n") }
            };

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                result, path, outputFolder, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Contains(Path.Combine(outputFolder, AnalysisArtifactWriter.BuildPropFileName), artifacts.Files);
        }

        [Fact]
        public async Task AnalysisWithoutFindingsSavesOnlyTheReport()
        {
            // Ни build.prop, ни лицензии в дампе не нашлось — сохранять нечего, кроме
            // самого отчёта о разборе. Раньше в этом случае не создавалось ничего.
            string path = WriteDump(new DumpBuilder(0x1000).Build());

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                new PartitionAnalysisResult { Status = PartitionAnalysisStatus.Completed },
                path, "", new SilentAnalysisHost(), CancellationToken.None);

            Assert.Equal(new[] { Path.Combine(_folder, AnalysisArtifactWriter.AnalysisFileName) }, artifacts.Files);
        }

        [Fact]
        public async Task ADumpThatDisappearedDoesNotBreakTheRest()
        {
            // Лицензия читается из дампа заново, и к этому моменту файла может уже не быть.
            // Это не повод терять build.prop, который уже в памяти.
            string path = Path.Combine(_folder, "gone.bin");

            var result = new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                Android = new AndroidInfo { RawBuildProp = Encoding.ASCII.GetBytes("ro.product.model=TV\n") },
                DuneLicense = new DuneLicense(0x1000, 16)
            };

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                result, path, _folder, new SilentAnalysisHost(), CancellationToken.None);

            Assert.Contains(Path.Combine(_folder, AnalysisArtifactWriter.BuildPropFileName), artifacts.Files);
            Assert.DoesNotContain(Path.Combine(_folder, AnalysisArtifactWriter.DuneLicenseFileName), artifacts.Files);
        }

        // ---------- отчёт о разборе ----------

        [Fact]
        public async Task WritesTheAnalysisReportNextToTheDump()
        {
            // Отчёт нужен для одного: прислать пару килобайт вместо дампа на гигабайты.
            // Поэтому проверяется не «файл создан», а то, что в нём есть всё, по чему
            // можно понять разбор, не открывая сам дамп.
            string path = WriteDump(new DumpBuilder(0x4000).Build());

            var table = new PartitionTable();
            table.Add("boot", 0x1000, 0x1000, FsType.Ext4, comment: "первый");
            table.Add("data", 0x2000, 0x9000);

            var result = new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                Table = table,
                MarkName = "MBR",
                LogicalSize = 0x4000,
                Geometry = new NandGeometry(2048, 64, 0x20, 0x01),
                Android = new AndroidInfo { Brand = "Sony", Model = "KD-43", Release = "9", Platform = "mt5891" },
                Issues = PartitionTableValidator.Validate(table, 0x4000)
            };

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                result, path, _folder, new SilentAnalysisHost(), CancellationToken.None);

            string reportPath = Path.Combine(_folder, AnalysisArtifactWriter.AnalysisFileName);
            Assert.Contains(reportPath, artifacts.Files);

            using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = json.RootElement;

            Assert.Equal(AnalysisArtifactWriter.AnalysisSchemaVersion, root.GetProperty("schema").GetInt32());
            // Только имя файла: полный путь содержит имя пользователя, а пересылать отчёт
            // будут посторонним.
            Assert.Equal("dump.bin", root.GetProperty("dump").GetString());
            Assert.Equal("Completed", root.GetProperty("status").GetString());
            Assert.Equal("MBR", root.GetProperty("detector").GetString());
            Assert.Equal(2048, root.GetProperty("nand").GetProperty("main").GetInt32());
            Assert.Equal("Sony", root.GetProperty("android").GetProperty("brand").GetString());

            var partitions = root.GetProperty("partitions");
            Assert.Equal(2, partitions.GetArrayLength());
            Assert.Equal("boot", partitions[0].GetProperty("name").GetString());
            Assert.Equal(0x1000, partitions[0].GetProperty("offset").GetInt64());
            Assert.Equal("Ext4", partitions[0].GetProperty("fileSystem").GetString());

            // Раздел data уходит за конец дампа — замечание об этом тоже в отчёте,
            // и записано именем причины, а не переведённой строкой.
            var issues = root.GetProperty("issues");
            Assert.Equal(1, issues.GetArrayLength());
            Assert.Equal(nameof(PartitionIssueKind.EndsBeyondDump), issues[0].GetProperty("kind").GetString());
            Assert.Equal("data", issues[0].GetProperty("name").GetString());
        }

        [Fact]
        public async Task WritesTheReportEvenWhenNoLayoutWasRecognised()
        {
            // Как раз этот случай и присылают: программа ничего не нашла, и надо понять
            // почему. Отчёт здесь — единственное, что можно посмотреть вместо дампа.
            string path = WriteDump(new DumpBuilder(0x4000).Build());

            var result = new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.LayoutNotRecognised,
                LogicalSize = 0x4000
            };

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                result, path, _folder, new SilentAnalysisHost(), CancellationToken.None);

            string reportPath = Path.Combine(_folder, AnalysisArtifactWriter.AnalysisFileName);
            Assert.Contains(reportPath, artifacts.Files);

            using var json = JsonDocument.Parse(File.ReadAllText(reportPath));

            Assert.Equal("LayoutNotRecognised", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("partitions").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("detector").ValueKind);
        }

        [Fact]
        public async Task NoReportForAnInterruptedAnalysis()
        {
            // Отменённый разбор ничего не выяснил: отчёт о нём только сбивал бы с толку.
            string path = WriteDump(new DumpBuilder(0x4000).Build());

            var artifacts = await AnalysisArtifactWriter.WriteAsync(
                new PartitionAnalysisResult { Status = PartitionAnalysisStatus.Cancelled },
                path, _folder, new SilentAnalysisHost(), CancellationToken.None);

            // Ни отчёта, ни чего-либо ещё — и это не ошибка.
            Assert.False(artifacts.Any);
        }

        [Fact]
        public async Task ReportKeepsCyrillicNamesReadable()
        {
            // Имя раздела может прийти из таблицы как угодно; экранированное в \uXXXX
            // оно превращает отчёт в нечитаемый, а его читают глазами.
            string path = WriteDump(new DumpBuilder(0x4000).Build());

            var table = new PartitionTable();
            table.Add("раздел", 0, 0x1000);

            await AnalysisArtifactWriter.WriteAsync(
                new PartitionAnalysisResult { Status = PartitionAnalysisStatus.Completed, Table = table, LogicalSize = 0x4000 },
                path, _folder, new SilentAnalysisHost(), CancellationToken.None);

            string text = File.ReadAllText(Path.Combine(_folder, AnalysisArtifactWriter.AnalysisFileName));

            Assert.Contains("раздел", text);
        }

        [Fact]
        public void PhilipsSuggestedNamesAreUsableAsFileNames()
        {
            // Косая черта в обозначении модели — обычное дело у Philips, а в имени файла
            // её быть не может.
            var firmware = new PhilipsFirmwareInfo("55PFL6158K/12", "QF2EU-0.173.65.0", "ZH1H1335007420");

            Assert.DoesNotContain('/', firmware.SuggestedFileName);
            Assert.EndsWith(".bin", firmware.SuggestedFileName);

            var eeprom = new Philips24C64.Content("55PFL6158K/12", "ZH1H1335007420", Array.Empty<int>());

            Assert.DoesNotContain('/', eeprom.SuggestedFileName);
            Assert.EndsWith("24C64.bin", eeprom.SuggestedFileName);
        }
    }
}
