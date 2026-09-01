using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что именно извлекается — от этого зависит имя папки с результатом.</summary>
    public enum ExtractionSource
    {
        /// <summary>Разделы из таблицы разметки.</summary>
        PartitionTable,

        /// <summary>Тома файловых систем, найденные вторым проходом.</summary>
        FileSystems
    }

    public enum PartitionExtractStatus
    {
        SourceNotFound,
        NothingSelected,
        NotEnoughSpace,
        OutputNotUsable,
        Cancelled,
        Failed,
        Completed
    }

    public sealed class PartitionExtractRequest
    {
        public string SourcePath { get; init; } = "";

        /// <summary>Куда складывать. Пусто — рядом с дампом, в папку с его именем.</summary>
        public string OutputFolder { get; init; } = "";

        public ExtractionSource Source { get; init; } = ExtractionSource.PartitionTable;

        /// <summary>Геометрия страницы; для eMMC — <c>null</c>.</summary>
        public NandGeometry? Geometry { get; init; }

        /// <summary>Нестандартная раскладка страницы Blu-ray, если она была определена.</summary>
        public BluerayPageLayout? BluerayLayout { get; init; }

        public bool CheckDiskSpace { get; init; } = true;
    }

    public sealed class PartitionExtractOutcome
    {
        public PartitionExtractStatus Status { get; init; }

        /// <summary>Папка, в которую писали.</summary>
        public string OutputFolder { get; init; } = "";

        /// <summary>Папка с разделами, очищенными от служебных областей. Только для NAND.</summary>
        public string MainOnlyFolder { get; init; } = "";

        public IReadOnlyList<string> CreatedFiles { get; init; } = Array.Empty<string>();

        public int ExtractedCount { get; init; }

        public string ErrorMessage { get; init; } = "";
    }

    /// <summary>
    /// Извлечение разделов из дампа в отдельные файлы.
    ///
    /// Для дампа NAND создаются два набора: полный, где данные лежат вместе со служебными
    /// областями, и очищенный — только полезные байты. Первый годится, чтобы записать
    /// раздел обратно в микросхему как есть, второй — чтобы открыть его чем-нибудь,
    /// понимающим файловые системы.
    /// </summary>
    public static class PartitionExtractOperation
    {
        /// <summary>Размер буфера копирования.</summary>
        private const int CopyBufferSize = 1024 * 1024;

        private const string PartsFolderSuffix = "_parts_";
        private const string FileSystemsFolderSuffix = "_fs_";
        private const string MainOnlyFolderSuffix = "_onlyMain_";

        public static async Task<PartitionExtractOutcome> RunAsync(
            PartitionExtractRequest request,
            PartitionTable table,
            IAnalysisHost host,
            CancellationToken ct = default,
            PauseController? pause = null)
        {
            if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
                return new PartitionExtractOutcome { Status = PartitionExtractStatus.SourceNotFound };

            var selected = new List<PartitionEntry>();
            foreach (var part in table.Items)
                if (part.Selected && part.Length > 0) selected.Add(part);

            if (selected.Count == 0)
                return new PartitionExtractOutcome { Status = PartitionExtractStatus.NothingSelected };

            var created = new List<string>();

            try
            {
                return await ExtractAsync(request, selected, created, host, ct, pause).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Недописанный файл выглядит как готовый раздел, а данные в нём неполные —
                // такие обязательно убираем.
                IncompleteOutput.Remove(created, message => host.Log(message, AnalysisLogLevel.Warning));
                return new PartitionExtractOutcome { Status = PartitionExtractStatus.Cancelled };
            }
            catch (Exception ex)
            {
                IncompleteOutput.Remove(created, message => host.Log(message, AnalysisLogLevel.Warning));
                return new PartitionExtractOutcome
                {
                    Status = IncompleteOutput.IsDiskFull(ex)
                        ? PartitionExtractStatus.NotEnoughSpace
                        : PartitionExtractStatus.Failed,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static async Task<PartitionExtractOutcome> ExtractAsync(
            PartitionExtractRequest request,
            List<PartitionEntry> selected,
            List<string> created,
            IAnalysisHost host,
            CancellationToken ct,
            PauseController? pause)
        {
            string folder = ResolveFolder(request);
            string mainOnlyFolder = request.Geometry is null
                ? ""
                : SuffixedFolder(request.SourcePath, request.OutputFolder, MainOnlyFolderSuffix);

            // Сколько всего байт будет записано. Считается всегда, а не только под проверку
            // места: это же число — знаменатель прогресса, и считать его дважды разными
            // способами значило бы однажды получить полосу, которая не сходится
            // с зарезервированным местом.
            long totalBytes = EstimateSize(selected, request.Geometry);

            if (request.CheckDiskSpace)
            {
                var space = DiskSpaceHelper.CheckSpace(folder, totalBytes);
                if (!space.HasEnoughSpace)
                {
                    return new PartitionExtractOutcome
                    {
                        Status = PartitionExtractStatus.NotEnoughSpace,
                        OutputFolder = folder,
                        ErrorMessage = Strings.Format("Extract_NotEnoughSpace",
                            DiskSpaceHelper.FormatBytes(totalBytes), DiskSpaceHelper.FormatBytes(space.AvailableBytes))
                    };
                }
            }

            Directory.CreateDirectory(folder);
            if (mainOnlyFolder.Length > 0) Directory.CreateDirectory(mainOnlyFolder);

            using var file = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize);

            IDumpReader reader = request.Geometry is null
                ? new PlainDumpReader(file, ownsStream: false)
                : new NandDumpReader(file, request.Geometry, ownsStream: false) { Blueray = request.BluerayLayout };

            using (reader)
            {
                int done = 0;

                // О ходе работы сообщаем в записанных байтах, а не в номерах разделов.
                //
                // По разделам полоса врала. В дампе телевизора их три-четыре десятка,
                // и почти все крошечные — boot, dtb, misc, — а вес несут один-два: system
                // и userdata по нескольку гигабайт. Шаг полосы был один раздел независимо
                // от размера, поэтому она добегала до девяноста с лишним процентов за пару
                // секунд и потом стояла на месте всё время записи последнего раздела.
                // Отличить это от зависшей программы было нельзя — та же беда, ради которой
                // в «Конвертировании» появилась строка «Закрываю файл…».
                //
                // Прореживать эти сообщения, в отличие от проходов разбора, не нужно:
                // каждому предшествует запись мегабайта на диск, то есть частоту
                // ограничивает сама запись, а не скорость цикла в памяти.
                long written = 0;
                string stage = "";

                void CountWritten(int bytes)
                {
                    written += bytes;
                    host.Progress?.Report(new AnalysisProgress(stage, written, totalBytes));
                }

                foreach (var part in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

                    // Подпись под полосой — имя раздела, который пишется сейчас.
                    stage = part.Name;
                    host.Progress?.Report(new AnalysisProgress(stage, written, totalBytes));

                    if (request.Geometry is { } geometry)
                    {
                        // Полный файл: байты как они лежат в дампе, вместе со служебными
                        // областями. Такой можно записать обратно в микросхему как есть.
                        string physicalName = PartitionFileNaming.ForPartition(
                            geometry.AddSpare(part.Offset), geometry.AddSpare(part.Length), part.Name, part.FsType);

                        await WritePhysicalAsync(
                            reader, Path.Combine(folder, physicalName), created,
                            geometry.AddSpare(part.Offset), geometry.AddSpare(part.Length), ct, pause,
                            CountWritten).ConfigureAwait(false);

                        // Очищенный: только полезные байты.
                        string logicalName = PartitionFileNaming.ForPartition(part);
                        await WriteLogicalAsync(
                            reader, Path.Combine(mainOnlyFolder, logicalName), created,
                            part.Offset, part.Length, ct, pause, CountWritten).ConfigureAwait(false);
                    }
                    else
                    {
                        string name = PartitionFileNaming.ForPartition(part);
                        await WriteLogicalAsync(
                            reader, Path.Combine(folder, name), created,
                            part.Offset, part.Length, ct, pause, CountWritten).ConfigureAwait(false);
                    }

                    done++;
                    host.Log(Strings.Format("Extract_PartitionWritten", part.Name), AnalysisLogLevel.Found);
                }

                // Раздел, объявленный длиннее дампа, пишется обрезанным — записанных байт
                // тогда меньше обещанных, и полоса замерла бы, не дойдя до конца. Видно её
                // в этот момент хорошо: окно с итогом появляется раньше, чем вкладка
                // сбрасывает прогресс.
                host.Progress?.Report(new AnalysisProgress(stage, totalBytes, totalBytes));

                return new PartitionExtractOutcome
                {
                    Status = PartitionExtractStatus.Completed,
                    OutputFolder = folder,
                    MainOnlyFolder = mainOnlyFolder,
                    CreatedFiles = created,
                    ExtractedCount = done
                };
            }
        }

        /// <summary>
        /// Пишет полезные байты раздела — без служебных областей.
        /// <paramref name="onWritten"/> вызывается после каждого записанного куска: по нему
        /// вкладка считает общий прогресс.
        /// </summary>
        private static async Task WriteLogicalAsync(
            IDumpReader reader, string path, List<string> created,
            long offset, long length, CancellationToken ct, PauseController? pause,
            Action<int>? onWritten = null)
        {
            created.Add(path);

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
            var buffer = new byte[CopyBufferSize];

            long remaining = Math.Min(length, Math.Max(0, reader.LogicalSize - offset));
            long at = offset;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

                int chunk = (int)Math.Min(buffer.Length, remaining);
                int read = reader.Read(at, buffer.AsSpan(0, chunk));
                if (read <= 0) break;

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                at += read;
                remaining -= read;
                onWritten?.Invoke(read);
            }
        }

        /// <summary>
        /// Пишет байты так, как они лежат в файле дампа.
        /// <paramref name="onWritten"/> — то же, что и в <see cref="WriteLogicalAsync"/>.
        /// </summary>
        private static async Task WritePhysicalAsync(
            IDumpReader reader, string path, List<string> created,
            long offset, long length, CancellationToken ct, PauseController? pause,
            Action<int>? onWritten = null)
        {
            created.Add(path);

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
            var buffer = new byte[CopyBufferSize];

            long remaining = Math.Min(length, Math.Max(0, reader.PhysicalSize - offset));
            long at = offset;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

                int chunk = (int)Math.Min(buffer.Length, remaining);
                int read = reader.ReadPhysical(at, buffer.AsSpan(0, chunk));
                if (read <= 0) break;

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                at += read;
                remaining -= read;
                onWritten?.Invoke(read);
            }
        }

        private static long EstimateSize(List<PartitionEntry> selected, NandGeometry? geometry)
        {
            long total = 0;
            foreach (var part in selected)
            {
                total += part.Length;
                // Для NAND пишутся два файла: полный и очищенный.
                if (geometry is not null) total += geometry.AddSpare(part.Length);
            }
            return total;
        }

        private static string ResolveFolder(PartitionExtractRequest request)
        {
            string suffix = request.Source == ExtractionSource.FileSystems
                ? FileSystemsFolderSuffix
                : PartsFolderSuffix;

            return SuffixedFolder(request.SourcePath, request.OutputFolder, suffix);
        }

        /// <summary>
        /// Папка рядом с дампом, названная по нему же. Оригинал выбирал суффикс по
        /// глобальному флагу, который к моменту извлечения хранил результат прошлого
        /// разбора, — и из-за путаницы в условии результат поиска файловых систем попадал
        /// в папку с разделами и наоборот. Здесь суффикс приходит явным параметром.
        /// </summary>
        private static string SuffixedFolder(string sourcePath, string outputFolder, string suffix)
        {
            string baseFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "."
                : outputFolder;

            string name = Path.GetFileNameWithoutExtension(sourcePath);
            return Path.Combine(baseFolder, name + suffix);
        }
    }
}
