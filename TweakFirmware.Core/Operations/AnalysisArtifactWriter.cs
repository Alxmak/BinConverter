using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
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
