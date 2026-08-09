using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Чем кончился разбор дампа.</summary>
    public enum PartitionAnalysisStatus
    {
        /// <summary>Файла по указанному пути нет.</summary>
        SourceNotFound,

        /// <summary>Файл прочитать не удалось.</summary>
        Failed,

        /// <summary>Отменено пользователем.</summary>
        Cancelled,

        /// <summary>Это не дамп прошивки, а память настроек Philips 24C64.</summary>
        EepromRecognised,

        /// <summary>Разметку опознать не удалось; список разделов пуст.</summary>
        LayoutNotRecognised,

        /// <summary>Разделы найдены.</summary>
        Completed
    }

    /// <summary>Что разбирать и с какими оговорками.</summary>
    public sealed class PartitionAnalysisRequest
    {
        public string SourcePath { get; init; } = "";

        /// <summary>
        /// Геометрия страницы, заданная человеком. Нужна в одном случае: когда на разбор
        /// подан не дамп, а текстовый файл со списком разделов — вывести из него размер
        /// страницы неоткуда, а размеры в таком списке бывают заданы без учёта служебных
        /// областей.
        /// </summary>
        public NandGeometry? ManualGeometry { get; init; }
    }

    /// <summary>Итог разбора.</summary>
    public sealed class PartitionAnalysisResult
    {
        public PartitionAnalysisStatus Status { get; init; }

        public PartitionTable Table { get; init; } = new();

        /// <summary>Имя опознанной разметки или <c>null</c>.</summary>
        public string? MarkName { get; init; }

        public NandGeometry? Geometry { get; init; }

        /// <summary>Размер данных без служебных областей — от него считаны границы.</summary>
        public long LogicalSize { get; init; }

        public AndroidInfo? Android { get; init; }

        public PhilipsFirmwareInfo? Philips { get; init; }

        public Philips24C64.Content? Eeprom { get; init; }

        /// <summary>Место файла лицензии Dune HD в дампе, если он нашёлся.</summary>
        public DuneLicense DuneLicense { get; init; }

        public string ErrorMessage { get; init; } = "";
    }

    /// <summary>
    /// Разбор дампа прошивки: найти разметку, прочитать разделы, при желании заглянуть
    /// внутрь них и опознать файловые системы.
    ///
    /// Порядок опроса детекторов — часть алгоритма. Он подобран так, что узкие и надёжные
    /// признаки проверяются раньше широких, и первое же совпадение останавливает перебор.
    /// Цепочек две, и вторая выполняется отдельно от первой: у дампа NAND может найтись,
    /// скажем, таблица MBR, но настоящая разметка при этом будет своя, и детекторы NAND
    /// всё равно должны отработать. Так было в оригинале, и на реальных дампах это даёт
    /// рабочий результат.
    /// </summary>
    public static class PartitionAnalysisOperation
    {
        /// <summary>
        /// Файл меньше этого размера считается не дампом, а текстовым списком разделов —
        /// строкой параметров ядра, сохранённой в файл.
        /// </summary>
        private const long MaxPartitionListFileSize = 10_000;

        /// <summary>Разметки, которые могут встретиться в любом дампе.</summary>
        public static IReadOnlyList<ILayoutDetector> CreateCommonChain() => new ILayoutDetector[]
        {
            new NorFlashDetector(),
            new ChinaAndroidDetector(),
            new SonyMtkEmmcDetector(),
            new MbrDetector(),
            new MtkLgDetector(),
            new MtkAndroidDetector(),
            new MptAmlogicDetector(),
            new RockchipPx3Detector(),
            new ReceiverAndroidDetector()
        };

        /// <summary>Разметки, которые бывают только у страничной памяти.</summary>
        public static IReadOnlyList<ILayoutDetector> CreateNandChain() => new ILayoutDetector[]
        {
            new AmbarellaDetector(),
            new BluerayNandDetector(),
            new NovatekDetector(),
            new NvtNandDetector(),
            new HiSiliconNandDetector(),
            new DuneHdDetector(),
            new PhilipsQfuDetector(),
            new LgNandDetector(),
            new SatReceiverDetector(),
            new MstarNandDetector(),
            new MediatekNandDetector(),
            new SamsungNandDetector()
        };

        public static async Task<PartitionAnalysisResult> RunAsync(
            PartitionAnalysisRequest request,
            IAnalysisHost host,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
                return new PartitionAnalysisResult { Status = PartitionAnalysisStatus.SourceNotFound };

            try
            {
                return await AnalyseAsync(request, host, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new PartitionAnalysisResult { Status = PartitionAnalysisStatus.Cancelled };
            }
            catch (Exception ex)
            {
                return new PartitionAnalysisResult
                {
                    Status = PartitionAnalysisStatus.Failed,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static async Task<PartitionAnalysisResult> AnalyseAsync(
            PartitionAnalysisRequest request, IAnalysisHost host, CancellationToken ct)
        {
            using var file = new FileStream(
                request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

            host.Log(Strings.Format("Extract_FileSize", $"0x{file.Length:X}", file.Length));

            // Память настроек Philips — это не дамп прошивки, разделов в ней нет.
            if (file.Length == Philips24C64.Size)
            {
                using var small = new PlainDumpReader(file, ownsStream: false);
                var eeprom = Philips24C64.TryRead(small);
                if (eeprom is not null)
                {
                    host.Log(Strings.Format("Extract_PhilipsEeprom", eeprom.Model, eeprom.Serial), AnalysisLogLevel.Found);
                    return new PartitionAnalysisResult
                    {
                        Status = PartitionAnalysisStatus.EepromRecognised,
                        Eeprom = eeprom
                    };
                }
            }

            // Маленький файл — это, скорее всего, сохранённая строка параметров ядра,
            // а не дамп: так удобно разбирать список разделов, скопированный из логов.
            if (file.Length < MaxPartitionListFileSize)
                return ReadPartitionListFile(file, request, host);

            var geometry = await NandGeometryDetector.DetectAsync(file, host, ct).ConfigureAwait(false);

            IDumpReader baseReader = geometry is null
                ? new PlainDumpReader(file, ownsStream: false)
                : new NandDumpReader(file, geometry, ownsStream: false);

            using var dump = new CachingDumpReader(baseReader);
            var context = new DumpContext(dump);
            var table = new PartitionTable();

            // Первая цепочка: первое совпадение останавливает перебор.
            RunChain(CreateCommonChain(), dump, baseReader, context, table, host, ct);

            // Вторая цепочка — отдельно, а не продолжением первой.
            if (geometry is not null)
                RunChain(CreateNandChain(), dump, baseReader, context, table, host, ct);

            if (table.Count == 0)
            {
                host.Log(Strings.Get("Extract_LayoutNotRecognised"), AnalysisLogLevel.Warning);
                return new PartitionAnalysisResult
                {
                    Status = PartitionAnalysisStatus.LayoutNotRecognised,
                    Geometry = geometry,
                    LogicalSize = dump.LogicalSize,
                    Philips = context.PhilipsInfo
                };
            }

            context.LogicalSize = PartitionTableNormalizer.Normalize(table, context.LogicalSize);
            AttachBadBlocks(table, context);

            host.Log(Strings.Format("Extract_PartsTotal", table.Count), AnalysisLogLevel.Found);

            AndroidInfo? android = null;

            // Второй проход по желанию: он куда дольше первого, потому что читает дамп
            // целиком, а не по нескольким адресам.
            if (await host.ConfirmAsync(Strings.Get("Extract_SearchFileSystemsQuestion"), ct).ConfigureAwait(false))
            {
                FileSystemScanner.ScanPartitions(dump, table, host, ct);

                context.LogicalSize = PartitionTableNormalizer.Normalize(table, context.LogicalSize);
                PartitionTableNormalizer.MarkEmpty(table, dump, host.Progress, ct);
                AttachBadBlocks(table, context);

                host.Log(Strings.Format("Extract_PartsTotal", table.Count), AnalysisLogLevel.Found);

                android = AndroidInfoReader.TryRead(dump, table, host, ct);
            }

            return new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                Table = table,
                MarkName = context.MarkName,
                Geometry = geometry,
                LogicalSize = context.LogicalSize,
                Android = android,
                Philips = context.PhilipsInfo,
                DuneLicense = context.DuneLicense
            };
        }

        /// <summary>
        /// Прогоняет цепочку детекторов до первого совпадения и читает разделы найденной
        /// разметкой.
        /// </summary>
        private static void RunChain(
            IReadOnlyList<ILayoutDetector> chain,
            CachingDumpReader dump,
            IDumpReader baseReader,
            DumpContext context,
            PartitionTable table,
            IAnalysisHost host,
            CancellationToken ct)
        {
            foreach (var detector in chain)
            {
                ct.ThrowIfCancellationRequested();

                var detection = detector.TryDetect(dump, context, host, ct);
                if (detection is null) continue;

                host.Log(Strings.Format("Extract_LayoutFound", detector.Name), AnalysisLogLevel.Found);
                context.MarkName = context.MarkName is null ? detector.Name : $"{context.MarkName} + {detector.Name}";

                // Детектор BLUERAY по ходу опознания выясняет раскладку страницы. Пока она
                // не применена, дамп читается неправильно, поэтому применяем сразу и
                // сбрасываем кэш: по тем же логическим адресам теперь другие данные.
                if (context.BluerayLayout is { } layout && baseReader is NandDumpReader nand && nand.Blueray is null)
                {
                    nand.Blueray = layout;
                    dump.Reset();
                }

                detector.ReadParts(detection, table, dump, context, host, ct);
                return;
            }
        }

        private static void AttachBadBlocks(PartitionTable table, DumpContext context)
        {
            if (context.BadBlocks.Count == 0 || context.NandBlockSize <= 0) return;

            PartitionTableNormalizer.AttachBadBlocks(table, context.BadBlocks, context.NandBlockSize);
        }

        /// <summary>
        /// Разбор текстового файла со списком разделов. Размеры в таких списках заданы
        /// без учёта служебных областей, и пересчитать их можно, только если человек
        /// скажет размер страницы: из самого файла его не вывести.
        /// </summary>
        private static PartitionAnalysisResult ReadPartitionListFile(
            FileStream file, PartitionAnalysisRequest request, IAnalysisHost host)
        {
            var content = new byte[file.Length];
            file.Position = 0;
            file.ReadExactly(content);

            // Строка кончается нулём или пробелом — так же, как когда её вырезают из дампа.
            int end = 0;
            while (end < content.Length && content[end] != 0 && content[end] != 0x20) end++;

            string text = Encoding.ASCII.GetString(content, 0, end);
            var table = new PartitionTable();

            if (MtdPartsParser.ParseInto(text, table) == 0)
            {
                host.Log(Strings.Get("Extract_LayoutNotRecognised"), AnalysisLogLevel.Warning);
                return new PartitionAnalysisResult { Status = PartitionAnalysisStatus.LayoutNotRecognised };
            }

            host.Log(Strings.Format("Extract_LayoutFound", "mtdparts"), AnalysisLogLevel.Found);

            long logicalSize = 0;
            foreach (var part in table.Items)
                if (part.Length > 0) logicalSize = Math.Max(logicalSize, part.Offset + part.Length);

            logicalSize = PartitionTableNormalizer.Normalize(table, logicalSize);

            if (request.ManualGeometry is { } geometry)
            {
                foreach (var part in table.Items)
                {
                    part.Offset = geometry.AddSpare(part.Offset);
                    if (part.Length > 0) part.Length = geometry.AddSpare(part.Length);
                }

                host.Log(Strings.Format("Extract_NandGeometryFound", geometry.Main, geometry.Spare));
            }

            host.Log(Strings.Format("Extract_PartsTotal", table.Count), AnalysisLogLevel.Found);

            return new PartitionAnalysisResult
            {
                Status = PartitionAnalysisStatus.Completed,
                Table = table,
                MarkName = "mtdparts",
                Geometry = request.ManualGeometry,
                LogicalSize = logicalSize
            };
        }
    }
}
