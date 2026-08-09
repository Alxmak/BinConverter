using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Android-телевизоры на MSTAR. Таблица разделов лежит сразу за первым сектором и
    /// устроена необычно: каждая запись занимает целый сектор и начинается собственной
    /// сигнатурой. Перебор идёт, пока сигнатура на месте.
    /// </summary>
    public sealed class ChinaAndroidDetector : ILayoutDetector
    {
        private const long TableOffset = 0x200;
        private const int TableLength = 0x10000;
        private const int RecordLength = 0x200;
        private const int MaxRecords = 64;
        private const ushort RecordSignature = 0x5840;
        private const long SectorSize = 0x200;

        public string Name => "MSTAR ANDROID";

        public int Priority => 40;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            if (!dump.Contains(TableOffset, RecordLength)) return null;

            int length = (int)Math.Min(TableLength, dump.LogicalSize - TableOffset);
            var table = dump.ReadBlock(TableOffset, length);

            if (BinaryPrimitives.ReadUInt16LittleEndian(table) != RecordSignature) return null;

            return new LayoutDetection(Name, TableOffset, table);
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

            for (int i = 0; i < MaxRecords; i++)
            {
                int record = i * RecordLength;
                if (record + RecordLength > data.Length) break;

                if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(record)) != RecordSignature) break;

                long offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x08)) * SectorSize;
                long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x0C)) * SectorSize;
                string name = DumpStrings.ReadFixed(data, record + 0x10, 32);

                // Область до первого раздела занята самой таблицей — её тоже полезно
                // получить отдельным файлом, иначе дамп потом не собрать обратно.
                if (i == 0) table.Add("Partitions_table", 0, offset);

                table.Add(name, offset, size);
            }

            host.Log(Strings.Format("Extract_PartsFromTable", table.Count), AnalysisLogLevel.Found);
        }
    }

    /// <summary>
    /// Приставки на Amlogic. Таблица помечена строкой MPT и лежит по одному
    /// фиксированному адресу.
    /// </summary>
    public sealed class MptAmlogicDetector : ILayoutDetector
    {
        private const long TableOffset = 0x2400000;
        private const int TableLength = 0x4000;

        /// <summary>Строка «MPT» с завершающим нулём, прочитанная как 32-битное число.</summary>
        private const uint Signature = 0x0054504D;

        private const int RecordsStart = 0x18;
        private const int RecordLength = 0x28;
        private const int CountOffset = 0x10;

        public string Name => "MPT AMLOGIC";

        public int Priority => 80;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            if (!dump.Contains(TableOffset, TableLength)) return null;

            var table = dump.ReadBlock(TableOffset, TableLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(table) != Signature) return null;

            return new LayoutDetection(Name, TableOffset, table);
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

            for (int i = 0; i < count; i++)
            {
                int record = RecordsStart + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                // Под имя отведено шестнадцать байт, но оригинал читает до тридцати двух,
                // обрываясь на первом нуле. Оставлено как было: у длинных имён в хвост
                // попадают байты размера, и их потом обрезает очистка имён.
                string name = DumpStrings.ReadFixed(data, record, 32);

                long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(record + 0x10));
                long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(record + 0x18));

                table.Add(name, offset, length);
            }

            host.Log(Strings.Format("Extract_PartsFromTable", count), AnalysisLogLevel.Found);
        }
    }

    /// <summary>
    /// Телевизоры LG на MediaTek. Таблица опознаётся по сигнатуре-дате и встречается в
    /// двух форматах: у старого поля смещения и размера 32-битные, у нового 64-битные.
    /// Ищется перебором по границам в 64 килобайта.
    /// </summary>
    public sealed class MtkLgDetector : ILayoutDetector
    {
        private const int TableLength = 0x4000;
        private const long ScanStep = 0x10000;
        private const int ScanCount = 256;

        /// <summary>Сигнатуры — это даты в виде ггггммдд.</summary>
        private const uint SignatureWide = 0x20120716;

        private const uint SignatureNarrow1 = 0x20110620;
        private const uint SignatureNarrow2 = 0x20110609;

        public string Name => "MTK LG";

        public int Priority => 65;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            for (int i = 0; i < ScanCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                long offset = ScanStep * i;
                if (!dump.Contains(offset, TableLength)) break;

                var table = dump.ReadBlock(offset, TableLength);
                uint signature = BinaryPrimitives.ReadUInt32LittleEndian(table);

                if (signature is SignatureWide or SignatureNarrow1 or SignatureNarrow2)
                    return new LayoutDetection(Name, offset, table);
            }

            return null;
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
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(data);
            int count = data[0x0C];

            string tableName = DumpStrings.ReadFixed(data, 0x10, 32);
            host.Log(Strings.Format("Extract_MtkLgTableFound", tableName, $"0x{detection.TableOffset:X}"), AnalysisLogLevel.Found);

            if (signature == SignatureWide)
                ReadWideRecords(data, table, count);
            else
                ReadNarrowRecords(data, table, count);
        }

        /// <summary>Формат 2012 года: запись 0x60 байт, смещение и размер по восемь байт.</summary>
        private static void ReadWideRecords(byte[] data, PartitionTable table, int count)
        {
            const int Start = 0x50;
            const int RecordLength = 0x60;

            for (int i = 0; i < count; i++)
            {
                int record = Start + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                string name = DumpStrings.ReadFixed(data, record, 32);
                long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(record + 0x20));
                long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(record + 0x28));
                string comment = DumpStrings.ReadFixed(data, record + 0x30, 32);

                table.Add(name, offset, length, FsType.Unknown, comment);
            }
        }

        /// <summary>Формат 2011 года: запись 0x58 байт, смещение и размер по четыре байта.</summary>
        private static void ReadNarrowRecords(byte[] data, PartitionTable table, int count)
        {
            const int Start = 0x48;
            const int RecordLength = 0x58;

            for (int i = 0; i < count; i++)
            {
                int record = Start + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                string name = DumpStrings.ReadFixed(data, record, 32);
                long offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x20));
                long length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 0x24));
                string comment = DumpStrings.ReadFixed(data, record + 0x28, 32);

                table.Add(name, offset, length, FsType.Unknown, comment);
            }
        }
    }
}
