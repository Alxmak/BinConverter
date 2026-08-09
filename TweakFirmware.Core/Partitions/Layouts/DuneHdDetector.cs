using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>Где в дампе Dune HD лежит файл лицензии и какой он длины.</summary>
    public readonly record struct DuneLicense(long Offset, int Length)
    {
        public bool Found => Length > 0;
    }

    /// <summary>
    /// Медиаплееры Dune HD.
    ///
    /// Разметка здесь не двоичная таблица, а текстовый конфиг: пары «ключ — значение»,
    /// где ключи называются _part1_offset, _part1_size, _part1_name и так далее. Сама
    /// раскладка страницы у этих плееров нестандартная и определяется раньше, при разборе
    /// геометрии, — сюда детектор попадает уже готовым к чтению.
    /// </summary>
    public sealed class DuneHdDetector : ILayoutDetector
    {
        /// <summary>У TV-102 конфиг лежит не в начале дампа, а по этому адресу.</summary>
        private const long Tv102ConfigOffset = 0xC0000;

        private const long Tv301ConfigOffset = 0x00000;

        private const string ProductKey = "product_id";
        private const string BoardKey = "board_id";
        private const string PartsCountKey = "_parts";

        public string Name => "DUNE HD";

        public int Priority => 255;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            // Раскладку страницы Dune HD определяет разбор геометрии: без неё дамп даже
            // не прочитать правильно, поэтому сюда мы попадаем уже с готовым ответом.
            if (context.Geometry is not { Layout: NandPageLayout.DuneHd } geometry) return null;

            long offset = geometry.DuneVariant == DuneHdVariant.Tv102 ? Tv102ConfigOffset : Tv301ConfigOffset;

            // Трёх страниц хватает на весь конфиг — так же читал и оригинал.
            int length = (int)Math.Min(geometry.Main * 3, dump.LogicalSize - offset);
            if (length <= 0) return null;

            var config = dump.ReadBlock(offset, length);
            if (DumpStrings.IndexOf(config, PartsCountKey) < 0) return null;

            return new LayoutDetection(Name, offset, config);
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            var config = detection.Table;

            LogValue(config, ProductKey, "Extract_DuneProductId", host);
            LogValue(config, BoardKey, "Extract_DuneBoardId", host);

            int countAt = DumpStrings.IndexOf(config, PartsCountKey);
            if (countAt < 0) return;

            // Число разделов записано двумя байтами сразу за ключом.
            int count = config[countAt + 7] | (config[countAt + 8] << 8);
            host.Log(Strings.Format("Extract_PartsFromTable", count), AnalysisLogLevel.Found);

            for (int i = 1; i <= count; i++)
            {
                long? offset = ReadNumber(config, $"_part{i}_offset");
                long? size = ReadNumber(config, $"_part{i}_size");
                if (offset is null || size is null) continue;

                string name = ReadText(config, $"_part{i}_name") ?? $"part_{i}";
                table.Add(name, offset.Value, size.Value);
            }

            // Файл лицензии ищем здесь же: он не входит ни в один раздел, и его место
            // нужно знать до того, как пользователь нажмёт «Извлечь».
            context.DuneLicense = FindLicense(dump, ct);
            if (context.DuneLicense.Found)
                host.Log(Strings.Format("Extract_DuneLicenseFound", context.DuneLicense.Length), AnalysisLogLevel.Found);
        }

        private static void LogValue(byte[] config, string key, string messageKey, IAnalysisHost host)
        {
            string? value = ReadText(config, key);
            if (value is not null) host.Log(Strings.Format(messageKey, value));
        }

        /// <summary>Значение ключа как 32-битное число: оно идёт сразу за ключом и разделителем.</summary>
        private static long? ReadNumber(byte[] config, string key)
        {
            int at = DumpStrings.IndexOf(config, key);
            if (at < 0) return null;

            int value = at + key.Length + 1;
            if (value + 4 > config.Length) return null;

            return BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan(value));
        }

        /// <summary>Значение ключа как строка.</summary>
        private static string? ReadText(byte[] config, string key)
        {
            int at = DumpStrings.IndexOf(config, key);
            if (at < 0) return null;

            string value = DumpStrings.ReadUntilNulOrSpace(config, at + key.Length + 1);
            return value.Length > 0 ? value : null;
        }

        /// <summary>
        /// Ищет в дампе файл лицензии плеера — <c>./dune_license.dlf</c>. Он лежит не в
        /// разделе, а просто в теле дампа, и при замене платы его переносят отдельно,
        /// поэтому оригинал извлекал его в отдельный файл.
        ///
        /// Возвращает место и длину содержимого; записывает файл уже операция извлечения:
        /// сам разбор дампа ничего на диск не пишет.
        /// </summary>
        public static DuneLicense FindLicense(IDumpReader dump, CancellationToken ct = default)
        {
            const string Marker = "./dune_license.dlf";

            // Поиск идёт по границам сектора: файл всегда выровнен.
            const int Step = 0x200;
            const int ChunkSize = 0x10000;

            var chunk = new byte[ChunkSize];

            for (long offset = 0; offset < dump.LogicalSize; offset += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();

                int read = dump.Read(offset, chunk);
                if (read <= 0) break;

                for (int at = 0; at + Marker.Length <= read; at += Step)
                {
                    if (DumpStrings.ReadFixed(chunk, at, Marker.Length) != Marker) continue;

                    // Содержимое лежит через сектор от имени и кончается нулевым байтом.
                    long contentAt = offset + at + Step;
                    int length = MeasureLicense(dump, contentAt);
                    return new DuneLicense(contentAt, length);
                }
            }

            return default;
        }

        private static int MeasureLicense(IDumpReader dump, long offset)
        {
            const int Limit = 0x4000;

            var data = dump.ReadBlock(offset, Limit);
            for (int i = 0; i < data.Length; i++)
                if (data[i] == 0) return i;

            return Limit;
        }
    }
}
