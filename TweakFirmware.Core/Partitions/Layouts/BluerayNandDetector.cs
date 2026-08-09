using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Плееры Blu-ray. Загрузчик занимает начало дампа и повторяет строку BOOTLOADER!
    /// в начале каждой записи — по ней разметка и опознаётся.
    ///
    /// Кроме таблицы разделов детектор выясняет раскладку страницы, потому что у части
    /// микросхем этого семейства она нестандартная: физическая страница состоит из
    /// нескольких подстраниц main+spare, за которыми идёт хвост из байт 0xFF. Найти это
    /// можно только по самому дампу — в служебных областях, которые обычное чтение прячет.
    /// </summary>
    public sealed class BluerayNandDetector : ILayoutDetector
    {
        private const string BootloaderMarker = "BOOTLOADER!";

        /// <summary>Записи загрузчика идут через 0x40 байт; проверяются первые двенадцать.</summary>
        private const int MarkerStride = 0x40;

        private const int MarkerArea = 0x1E0;

        /// <summary>Сколько байт с начала дампа читается для разбора раскладки.</summary>
        private const int ProbeLength = 0x8000;

        /// <summary>Подстраница: 1024 байта данных и 32 служебных.</summary>
        private const int SubpageMain = 0x400;

        private const int SubpageSpare = 0x20;
        private const int SubpageTotal = SubpageMain + SubpageSpare;

        private const long TableOffset = 0x200000;
        private const int TableLength = 0x20000;
        private const int RecordLength = 0x40;
        private const int CountOffset = 0x14;

        public string Name => "BLUERAY";

        public int Priority => 210;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            var head = new byte[ProbeLength];
            if (dump.ReadPhysical(0, head) < MarkerArea) return null;

            // Все двенадцать записей в начале дампа обязаны начинаться одной и той же
            // строкой: одиночное совпадение слишком легко встретить случайно.
            for (int offset = 0; offset < MarkerArea; offset += MarkerStride)
            {
                if (DumpStrings.ReadFixed(head, offset, BootloaderMarker.Length) != BootloaderMarker)
                    return null;
            }

            context.BluerayLayout = DetectPageLayout(head);
            if (context.BluerayLayout is { } layout)
                host.Log(Strings.Format("Extract_BluerayLayout", layout.SubpagesPerPage, layout.TrailingFf));

            return LayoutDetection.At(Name, TableOffset);
        }

        /// <summary>
        /// Выясняет, сколько подстраниц в физической странице и есть ли за ними хвост.
        ///
        /// Ориентир — первый участок из сплошных байт 0xFF: у этих микросхем он идёт
        /// ровно там, где кончаются подстраницы. Начало участка, делённое на размер
        /// подстраницы, и даёт их число, а длина участка — размер хвоста.
        /// </summary>
        private static BluerayPageLayout? DetectPageLayout(ReadOnlySpan<byte> head)
        {
            const int Granularity = 16;

            int start = -1;
            for (int offset = 0; offset + Granularity <= head.Length; offset += Granularity)
            {
                if (IsAllOnes(head.Slice(offset, Granularity)))
                {
                    start = offset;
                    break;
                }
            }

            if (start <= 0) return null;

            int end = start;
            while (end + 8 <= head.Length && IsAllOnes(head.Slice(end, 8))) end += 8;

            int subpages = start / SubpageTotal;
            if (subpages <= 0) return null;

            return new BluerayPageLayout(SubpageMain, SubpageSpare, subpages, end - start);
        }

        private static bool IsAllOnes(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
                if (b != 0xFF) return false;
            return true;
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            if (!dump.Contains(TableOffset, RecordLength)) return;

            int length = (int)Math.Min(TableLength, dump.LogicalSize - TableOffset);
            var data = dump.ReadBlock(TableOffset, length);

            int count = data[CountOffset];
            host.Log(Strings.Format("Extract_PartsFromTable", count), AnalysisLogLevel.Found);

            for (int i = 0; i < count; i++)
            {
                // Записи начинаются со второй позиции: первая занята заголовком таблицы.
                int record = RecordLength + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                string name = DumpStrings.ReadFixed(data, record, 32);
                long offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x18));
                long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x1C));

                table.Add(name, offset, size);
            }
        }
    }
}
