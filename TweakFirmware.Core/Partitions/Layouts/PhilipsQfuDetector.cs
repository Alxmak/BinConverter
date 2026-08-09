using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Сведения о прошивке Philips, вычитанные из дампа: модель аппарата, версия
    /// программы и серийный номер.
    /// </summary>
    public sealed record PhilipsFirmwareInfo(string Type, string SwVersion, string Serial)
    {
        /// <summary>
        /// Имя, под которым такой дамп принято хранить. Косая черта в обозначении модели
        /// заменяется подчёркиванием: в имени файла её быть не может.
        /// </summary>
        public string SuggestedFileName => $"{Type.Replace('/', '_')} {SwVersion} {Serial}.bin";
    }

    /// <summary>
    /// Телевизоры Philips на шасси QFUx.x с памятью NAND.
    ///
    /// Опознаётся по куску кода MIPS в самом начале дампа — таблицы или строки с
    /// сигнатурой у этой прошивки нет. Кроме разделов детектор вычитывает модель,
    /// версию и серийный номер: по ним дамп потом называют, чтобы не перепутать
    /// прошивки от разных аппаратов.
    /// </summary>
    public sealed class PhilipsQfuDetector : ILayoutDetector
    {
        private const long TableOffset = 0x66000;
        private const int RecordLength = 0x1D;
        private const int MaxRecords = 64;

        /// <summary>Область раздела RWBOOT, где лежит текстовое описание прошивки.</summary>
        private const long InfoScanStart = 0x1CC00000;

        private const long InfoScanEnd = 0x21400000;

        private const string VersionLabel = "SW version = ";
        private const string TypeLabel = "Set type   = ";
        private const string SerialLabel = "Set serial = ";
        private const int SerialLength = 14;

        /// <summary>
        /// Начало загрузочного кода. Это не сигнатура в привычном смысле, а первые
        /// восемьдесят байт машинного кода MIPS, одинаковые у всех прошивок этого шасси.
        /// </summary>
        private static readonly byte[] BootCode =
        {
            0x00, 0x68, 0x80, 0x40, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x60, 0x80, 0x40, 0xC0, 0x00, 0x00, 0x00,
            0x00, 0xBB, 0x03, 0x3C, 0x08, 0x20, 0x63, 0x34, 0x00, 0x33, 0x02, 0x3C, 0x02, 0x00, 0x42, 0x34,
            0x00, 0x00, 0x62, 0xAC, 0x00, 0xB5, 0x03, 0x3C, 0x49, 0xE8, 0x63, 0x34, 0x00, 0x00, 0x02, 0x24,
            0x00, 0x00, 0x62, 0xA0, 0x00, 0xBB, 0x03, 0x3C, 0x20, 0x20, 0x63, 0x34, 0x00, 0x00, 0x02, 0x24,
            0x00, 0x00, 0x62, 0xAC, 0x00, 0xBB, 0x03, 0x3C, 0x70, 0x20, 0x63, 0x34, 0x00, 0x00, 0x02, 0x24
        };

        public string Name => "QFUx.x";

        public int Priority => 260;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            var head = dump.ReadBlock(0, BootCode.Length);
            if (!head.AsSpan().SequenceEqual(BootCode)) return null;

            return LayoutDetection.At(Name, TableOffset);
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            for (int i = 0; i < MaxRecords; i++)
            {
                long at = TableOffset + (long)i * RecordLength;
                if (!dump.Contains(at, RecordLength)) break;

                var record = dump.ReadBlock(at, RecordLength);

                // Числа у этой прошивки записаны в обратном порядке байт.
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(record);
                if (offset == 0xFFFFFFFF) break;

                uint size = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4));
                string name = DumpStrings.ReadFixed(record, 0x12, 11);

                table.Add(name, offset, size);
            }

            host.Log(Strings.Format("Extract_PartsFromTable", table.Count), AnalysisLogLevel.Found);

            context.PhilipsInfo = ReadFirmwareInfo(dump, context, host, ct);
        }

        /// <summary>
        /// Ищет в разделе RWBOOT текстовое описание прошивки. Оно встречается в дампе
        /// несколько раз, поэтому одинаковые записи отбрасываются.
        /// </summary>
        private static PhilipsFirmwareInfo? ReadFirmwareInfo(
            IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            int pageSize = context.Geometry?.Main ?? 2048;
            long end = Math.Min(InfoScanEnd, dump.LogicalSize);
            if (InfoScanStart >= end) return null;

            var found = new List<PhilipsFirmwareInfo>();

            for (long offset = InfoScanStart; offset < end; offset += pageSize)
            {
                ct.ThrowIfCancellationRequested();

                if (((offset - InfoScanStart) / pageSize & 0xFF) == 0)
                    host.Progress?.Report(new AnalysisProgress("QFUx.x", offset - InfoScanStart, end - InfoScanStart));

                var page = dump.ReadBlock(offset, pageSize);
                if (DumpStrings.IndexOf(page, TypeLabel) < 0) continue;

                var info = ParseInfo(page);
                if (info is not null && !found.Contains(info)) found.Add(info);
            }

            if (found.Count == 0)
            {
                host.Log(Strings.Get("Extract_PhilipsInfoNotFound"), AnalysisLogLevel.Warning);
                return null;
            }

            foreach (var info in found)
                host.Log(Strings.Format("Extract_PhilipsInfo", info.Type, info.Serial, info.SwVersion), AnalysisLogLevel.Found);

            return found[0];
        }

        private static PhilipsFirmwareInfo? ParseInfo(byte[] page)
        {
            string? version = ReadLabelled(page, VersionLabel);
            string? type = ReadLabelled(page, TypeLabel);
            string? serial = ReadLabelled(page, SerialLabel, SerialLength);

            if (version is null || type is null || serial is null) return null;

            return new PhilipsFirmwareInfo(type, version, serial);
        }

        /// <summary>Значение помеченного поля: до конца строки либо заданной длины.</summary>
        private static string? ReadLabelled(byte[] page, string label, int fixedLength = 0)
        {
            int at = DumpStrings.IndexOf(page, label);
            if (at < 0) return null;

            int start = at + label.Length;
            int limit = fixedLength > 0 ? Math.Min(start + fixedLength, page.Length) : page.Length;

            int end = start;
            while (end < limit && page[end] >= 0x20) end++;

            return end > start ? System.Text.Encoding.ASCII.GetString(page, start, end - start) : null;
        }
    }

    /// <summary>
    /// Микросхема памяти 24C64 из телевизоров Philips — это не дамп прошивки, а
    /// восьмикилобайтная EEPROM с настройками. Разделов там нет, зато есть модель,
    /// серийный номер и набор опций, по которым аппарат настраивается.
    /// </summary>
    public static class Philips24C64
    {
        /// <summary>Размер такой микросхемы; файлы другого размера сюда не относятся.</summary>
        public const long Size = 0x2000;

        private const string Marker = "FUSIO";
        private const int MarkerOffset = 0x20;

        private const int ModelOffset = 0x10F8;
        private const int ModelLength = 13;
        private const int SerialOffset = 0x1106;
        private const int SerialLength = 14;

        private const int OptionsStart = 0x10E5;
        private const int OptionsEnd = 0x10F4;

        public sealed record Content(string Model, string Serial, IReadOnlyList<int> Options)
        {
            /// <summary>Имя, под которым такой дамп принято хранить.</summary>
            public string SuggestedFileName => $"{Model.Replace('/', '_')} {Serial} 24C64.bin";
        }

        public static Content? TryRead(IDumpReader dump)
        {
            if (dump.LogicalSize != Size) return null;

            var data = dump.ReadBlock(0, (int)Size);
            if (DumpStrings.ReadFixed(data, MarkerOffset, Marker.Length) != Marker) return null;

            string model = DumpStrings.ReadFixed(data, ModelOffset, ModelLength).Trim();
            string serial = DumpStrings.ReadFixed(data, SerialOffset, SerialLength).Trim();

            var options = new List<int>();
            for (int at = OptionsStart; at + 1 < OptionsEnd; at += 2)
                options.Add(data[at] | (data[at + 1] << 8));

            return new Content(model, serial, options);
        }
    }
}
