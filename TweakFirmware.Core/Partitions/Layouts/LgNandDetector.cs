using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>Какая из четырёх карт разделов LG опознана — от неё зависит разбор.</summary>
    public enum LgNandMap
    {
        None,

        /// <summary>Шасси на процессорах MStar.</summary>
        MStar,

        /// <summary>Шасси на Broadcom BCM35230.</summary>
        Bcm35230,

        /// <summary>Шасси на Broadcom BCM35xx: вся таблица записана обратными четвёрками байт.</summary>
        Bcm35xx,

        /// <summary>Шасси Douglas: таблица сдвинута на четыре байта, числа в обратном порядке.</summary>
        Douglas
    }

    /// <summary>
    /// Телевизоры LG с памятью NAND.
    ///
    /// Четыре разных карты разделов, которые опознаются по имени в начале таблицы. Две из
    /// них требуют возни с порядком байт: у BCM35xx перевёрнута вся таблица целиком,
    /// а у Douglas таблица вдобавок сдвинута на четыре байта относительно остальных.
    ///
    /// Кроме разделов, здесь читается таблица переназначения плохих блоков: она лежит в
    /// разделе bbminfo и говорит, какие блоки микросхемы вышли из строя. Мастеру это
    /// нужно, чтобы понимать, какие разделы дампа могли пострадать.
    /// </summary>
    public sealed class LgNandDetector : ILayoutDetector
    {
        private const long ScanStart = 0x20000;
        private const long ScanEnd = 0x200000;
        private const long ScanStep = 0x20000;
        private const long DouglasOffset = 0x60000;
        private const int TableLength = 0x2000;

        private const int RecordsStart = 0xF0;
        private const int RecordLength = 0x54;
        private const int CountOffset = 0x0D;

        /// <summary>Сигнатура таблицы плохих блоков в обоих встречающихся порядках байт.</summary>
        private const uint BbtSignature = 0xA6A59281;

        private const uint BbtSignatureSwapped = 0x8192A5A6;

        private LgNandMap _map;

        public string Name => "LG NAND";

        public int Priority => 265;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            _map = LgNandMap.None;

            for (long offset = ScanStart; offset <= ScanEnd; offset += ScanStep)
            {
                ct.ThrowIfCancellationRequested();

                if (!dump.Contains(offset, TableLength)) break;

                var candidate = dump.ReadBlock(offset, TableLength);
                _map = DumpStrings.ReadFixed(candidate, 0x10, 32) switch
                {
                    "mstar_map0" => LgNandMap.MStar,
                    "bcm35230_map0" => LgNandMap.Bcm35230,
                    "3mcb_xx50pam" => LgNandMap.Bcm35xx,
                    _ => LgNandMap.None
                };

                if (_map != LgNandMap.None)
                    return new LayoutDetection(Name, offset, Normalize(candidate, _map));
            }

            // Карта Douglas лежит по своему адресу, и имя в ней сдвинуто на четыре байта.
            if (dump.Contains(DouglasOffset, TableLength))
            {
                var candidate = dump.ReadBlock(DouglasOffset, TableLength);
                if (DumpStrings.ReadFixed(candidate, 0x0C, 32) == "douglas_map0")
                {
                    _map = LgNandMap.Douglas;
                    return new LayoutDetection(Name, DouglasOffset, Normalize(candidate, _map));
                }
            }

            return null;
        }

        /// <summary>
        /// Приводит таблицу к общему виду: у BCM35xx переворачивает каждую четвёрку байт,
        /// у Douglas сдвигает содержимое на четыре байта вправо. После этого все четыре
        /// карты читаются одним и тем же кодом.
        /// </summary>
        private static byte[] Normalize(byte[] table, LgNandMap map)
        {
            if (map == LgNandMap.Bcm35xx)
            {
                var swapped = (byte[])table.Clone();
                for (int i = 0; i + 4 <= swapped.Length; i += 4)
                {
                    (swapped[i], swapped[i + 3]) = (swapped[i + 3], swapped[i]);
                    (swapped[i + 1], swapped[i + 2]) = (swapped[i + 2], swapped[i + 1]);
                }
                return swapped;
            }

            if (map == LgNandMap.Douglas)
            {
                var shifted = (byte[])table.Clone();
                Array.Copy(table, 1, shifted, 5, table.Length - 5);
                return shifted;
            }

            return table;
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            var data = detection.Table;
            int count = data[CountOffset];

            host.Log(Strings.Format("Extract_LgNandMapFound", _map, $"0x{detection.TableOffset:X}"), AnalysisLogLevel.Found);
            host.Log(Strings.Format("Extract_PartsFromTable", count));

            for (int i = 0; i < count; i++)
            {
                int record = RecordsStart + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                string name = DumpStrings.ReadFixed(data, record, 32);
                string comment = DumpStrings.ReadFixed(data, record + 0x28, 32);

                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x20));
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x24));

                // У Douglas сами числа тоже записаны в обратном порядке байт — в отличие
                // от BCM35xx, где переворот всей таблицы это уже исправил.
                if (_map == LgNandMap.Douglas)
                {
                    offset = BinaryPrimitives.ReverseEndianness(offset);
                    length = BinaryPrimitives.ReverseEndianness(length);
                }

                table.Add(name, offset, length, FsType.Unknown, comment);
            }

            ReadBadBlockTable(table, dump, context, host);
        }

        /// <summary>
        /// Читает таблицу переназначения плохих блоков. У всех карт, кроме Douglas, она
        /// лежит в разделе bbminfo; у Douglas такого раздела нет, и таблица спрятана
        /// в разделе reserved.
        /// </summary>
        private void ReadBadBlockTable(PartitionTable table, IDumpReader dump, DumpContext context, IAnalysisHost host)
        {
            if (context.Geometry is not { } geometry) return;

            string wanted = _map == LgNandMap.Douglas ? "reserved" : "bbminfo";

            long offset = -1;
            foreach (var part in table.Items)
            {
                if (part.Name == wanted) offset = part.Offset;
            }

            if (offset < 0 || !dump.Contains(offset, geometry.Main)) return;

            var page = dump.ReadBlock(offset, geometry.Main);

            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(page);
            if (signature != BbtSignature && signature != BbtSignatureSwapped) return;

            bool swapped = _map is LgNandMap.Bcm35xx or LgNandMap.Douglas;

            uint count = ReadDword(page, 4, swapped);

            // Записей на одну больше, чем указано: даже когда плохих блоков нет, одна
            // запись в таблице всё равно есть.
            var remappedFrom = new System.Collections.Generic.List<long>();
            long firstRemappedTo = 0;

            for (uint i = 0; i <= count; i++)
            {
                int at = (int)(2 * i + 2) * 4;
                if (at + 8 > page.Length) break;

                uint to = ReadDword(page, at, swapped);
                uint from = ReadDword(page, at + 4, swapped);

                if (i == 0) firstRemappedTo = to;
                if (i < count) remappedFrom.Add(from);
            }

            if (firstRemappedTo == 0) return;

            // Общее число блоков в микросхеме выводится из номера резервного блока:
            // он лежит у самой верхней границы, поэтому округление вверх до степени
            // двойки даёт полный объём.
            long blocksInDump = 1L << BitLength(firstRemappedTo);
            context.NandBlockSize = dump.LogicalSize / blocksInDump;
            context.BadBlocks.AddRange(remappedFrom);

            host.Log(Strings.Format("Extract_BadBlocksFound", count));
            host.Log(Strings.Format("Extract_NandBlockSize", blocksInDump, $"0x{context.NandBlockSize:X}"));
        }

        private static uint ReadDword(ReadOnlySpan<byte> data, int offset, bool swapped)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            return swapped ? BinaryPrimitives.ReverseEndianness(value) : value;
        }

        private static int BitLength(long value)
        {
            int bits = 0;
            while (value > 0)
            {
                value >>= 1;
                bits++;
            }
            return bits;
        }
    }
}
