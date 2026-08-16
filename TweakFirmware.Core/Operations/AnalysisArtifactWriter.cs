using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что удалось вытащить из дампа помимо самих разделов.</summary>
    public sealed class AnalysisArtifacts
    {
        /// <summary>Пути записанных файлов.</summary>
        public List<string> Files { get; } = new();

        public bool Any => Files.Count > 0;
    }

    /// <summary>
    /// Запись побочных файлов, которые находятся при разборе дампа: build.prop из
    /// прошивки Android и файл лицензии плеера Dune HD.
    ///
    /// Отдельно от самого разбора намеренно. Разбор дампа только читает — так его можно
    /// прогнать на чужом файле, ничего не задев, и так он тестируется. Решение записать
    /// найденное на диск принимает тот, кто разбор запустил.
    /// </summary>
    public static class AnalysisArtifactWriter
    {
        /// <summary>Имя, под которым сохраняется найденный build.prop.</summary>
        public const string BuildPropFileName = "BuildProp.txt";

        /// <summary>Имя файла лицензии плеера — то же, что и внутри дампа.</summary>
        public const string DuneLicenseFileName = "dune_license.dlf";

        /// <summary>Имя отчёта о разборе.</summary>
        public const string AnalysisFileName = "analysis.json";

        /// <summary>
        /// Версия формата отчёта. Нужна тому, кто будет его читать: формат наверняка
        /// обрастёт полями, и по номеру видно, чего в старом файле ждать не стоит.
        /// </summary>
        public const int AnalysisSchemaVersion = 1;

        /// <summary>
        /// Пишет всё найденное рядом с дампом (или в указанную папку) и возвращает список
        /// созданных файлов. Ошибка записи одного файла не мешает остальным: это
        /// вспомогательные данные, из-за них разбор терять незачем.
        /// </summary>
        public static async Task<AnalysisArtifacts> WriteAsync(
            PartitionAnalysisResult result,
            string sourcePath,
            string outputFolder,
            IAnalysisHost host,
            CancellationToken ct = default)
        {
            var artifacts = new AnalysisArtifacts();

            string folder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "."
                : outputFolder;

            Directory.CreateDirectory(folder);

            if (result.Android is { RawBuildProp.Length: > 0 } android)
                await TryWriteAsync(Path.Combine(folder, BuildPropFileName), android.RawBuildProp, artifacts, host, ct)
                    .ConfigureAwait(false);

            if (result.DuneLicense.Found)
            {
                byte[]? license = ReadDuneLicense(sourcePath, result);
                if (license is not null)
                    await TryWriteAsync(Path.Combine(folder, DuneLicenseFileName), license, artifacts, host, ct)
                        .ConfigureAwait(false);
            }

            // Отчёт пишется и когда разметка не опознана: как раз тогда он и нужен —
            // это единственное, что можно прислать вместо самого дампа.
            if (result.Status is PartitionAnalysisStatus.Completed or PartitionAnalysisStatus.LayoutNotRecognised)
            {
                byte[] report = Encoding.UTF8.GetBytes(BuildReport(result, sourcePath));
                await TryWriteAsync(Path.Combine(folder, AnalysisFileName), report, artifacts, host, ct)
                    .ConfigureAwait(false);
            }

            return artifacts;
        }

        private static async Task TryWriteAsync(
            string path, byte[] content, AnalysisArtifacts artifacts, IAnalysisHost host, CancellationToken ct)
        {
            try
            {
                await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);
                artifacts.Files.Add(path);
                host.Log(Strings.Format("Extract_ArtifactSaved", path), AnalysisLogLevel.Found);
            }
            catch (OperationCanceledException)
            {
                // Отмена посреди записи оставляет обрезанный файл: выглядит он настоящим,
                // а внутри половина — и build.prop, прочитанный из такого, соврёт про
                // прошивку. Убираем так же, как за прерванным извлечением.
                IncompleteOutput.Remove(new[] { path }, message => host.Log(message, AnalysisLogLevel.Warning));
                throw;
            }
            catch (Exception ex)
            {
                host.Log(Strings.Format("Extract_ArtifactFailed", path, ex.Message), AnalysisLogLevel.Warning);
            }
        }

        // ============================= Отчёт о разборе =============================

        /// <summary>
        /// Что записывается в <see cref="AnalysisFileName"/>. Модель отдельная, а не
        /// сериализация внутренних типов: у отчёта своя жизнь — его читают снаружи,
        /// и он не должен меняться каждый раз, когда поменялось что-то внутри.
        ///
        /// Ни времени создания, ни полного пути к дампу здесь нет намеренно. Отчёты
        /// сравнивают между собой (в том числе как эталон для будущего набора реальных
        /// дампов), а метка времени делает одинаковые разборы разными файлами; полный
        /// путь — это ещё и имя пользователя в пути, которое ни к чему пересылать.
        /// </summary>
        private sealed record AnalysisReport(
            int Schema,
            string Dump,
            long FileSize,
            long LogicalSize,
            string Status,
            string? Detector,
            NandReport? Nand,
            AndroidReport? Android,
            IReadOnlyList<PartitionReport> Partitions,
            IReadOnlyList<IssueReport> Issues);

        private sealed record NandReport(int Main, int Spare, int Page, string Layout);

        private sealed record AndroidReport(string Brand, string Model, string Release, string Platform);

        private sealed record PartitionReport(
            int Index, string Name, long Offset, long Length, string FileSystem, string Comment, int BadBlocks);

        /// <summary>Замечание в машиночитаемом виде: имя причины, а не переведённая строка.</summary>
        private sealed record IssueReport(string Kind, string Name, long Offset, long Length, string Other);

        private static readonly JsonSerializerOptions ReportOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Кириллица в именах разделов иначе превращается в \uXXXX, и отчёт,
            // который писался ради чтения глазами, читать становится нечем.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Собирает отчёт. Наружу не торчит: проверять его надо тем же путём, каким
        /// он попадает к пользователю, — через запись файла.
        /// </summary>
        private static string BuildReport(PartitionAnalysisResult result, string sourcePath)
        {
            var report = new AnalysisReport(
                Schema: AnalysisSchemaVersion,
                Dump: Path.GetFileName(sourcePath),
                FileSize: FileSizeOf(sourcePath),
                LogicalSize: result.LogicalSize,
                Status: result.Status.ToString(),
                Detector: result.MarkName,
                Nand: result.Geometry is { } g
                    ? new NandReport(g.Main, g.Spare, g.Page, g.Layout.ToString())
                    : null,
                Android: result.Android is { } a
                    ? new AndroidReport(a.Brand, a.Model, a.Release, a.Platform)
                    : null,
                Partitions: result.Table.Items
                    .Select((part, index) => new PartitionReport(
                        index,
                        part.Name,
                        part.Offset,
                        part.Length,
                        part.FsType.ToString(),
                        part.Comment,
                        part.BadBlocks))
                    .ToList(),
                Issues: result.Issues
                    .Select(issue => new IssueReport(
                        issue.Kind.ToString(), issue.Name, issue.Offset, issue.Length, issue.OtherName))
                    .ToList());

            return JsonSerializer.Serialize(report, ReportOptions);
        }

        private static long FileSizeOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                // Размер — справочная величина: если файл уже недоступен, отчёт всё
                // равно стоит записать.
                return 0;
            }
        }

        /// <summary>
        /// Читает содержимое лицензии из дампа. Дамп на этот момент уже закрыт, поэтому
        /// открывается заново — файл маленький, и держать ради него поток открытым всё
        /// время между разбором и записью незачем.
        /// </summary>
        private static byte[]? ReadDuneLicense(string sourcePath, PartitionAnalysisResult result)
        {
            try
            {
                using var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                using IDumpReader dump = result.Geometry is null
                    ? new PlainDumpReader(file, ownsStream: false)
                    : new NandDumpReader(file, result.Geometry, ownsStream: false);

                return dump.ReadBlock(result.DuneLicense.Offset, result.DuneLicense.Length);
            }
            catch
            {
                // Лицензия — приятное дополнение, а не результат работы: если дамп уже
                // недоступен, молча обходимся без неё.
                return null;
            }
        }
    }
}
