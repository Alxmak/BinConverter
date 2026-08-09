using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Видеорегистраторы и камеры на процессорах Ambarella (A5, A7, A12, A20).
    ///
    /// Таблица лежит по фиксированному адресу и встречается в двух форматах, которые
    /// различаются только длиной записи и длиной поля имени. Оба начинаются одинаковой
    /// сигнатурой, поэтому проверяются подряд.
    /// </summary>
    public sealed class AmbarellaDetector : ILayoutDetector
    {
        private const long TableOffset = 0x21000;
        private const int TableLength = 0x400;

        /// <summary>Размер блока микросхемы: смещения и размеры в таблице заданы в блоках.</summary>
        private const long BlockSize = 0x20000;

        /// <summary>Значение в поле смещения, которым помечен конец таблицы.</summary>
        private const uint EndOfTable = 0x33219FBD;

        private static readonly byte[] ShortSignature =
        {
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x62, 0x73, 0x74, 0x00
        };

        private static readonly byte[] LongSignature =
        {
            0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x42, 0x6F, 0x6F, 0x74, 0x73, 0x74, 0x72, 0x61, 0x70
        };

        /// <summary>Какой формат записи подошёл: короткий с именем в восемь байт или длинный.</summary>
        private bool _longFormat;

        public string Name => "AMBARELLA";

        public int Priority => 200;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            if (!dump.Contains(TableOffset, TableLength)) return null;

            var table = dump.ReadBlock(TableOffset, TableLength);

            if (StartsWith(table, ShortSignature)) _longFormat = false;
            else if (StartsWith(table, LongSignature)) _longFormat = true;
            else return null;

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
            int recordLength = _longFormat ? 0x28 : 0x10;
            int nameLength = _longFormat ? 0x20 : 8;

            // Перебор ограничен размером прочитанного блока. В оригинале он полагался
            // только на метку конца таблицы, и повреждённая таблица уводила чтение
            // за пределы буфера.
            for (int record = 0; record + recordLength <= data.Length; record += recordLength)
            {
                uint offsetBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record));
                if (offsetBlocks == EndOfTable) break;

                uint sizeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 4));
                if (sizeBlocks == 0) continue;

                string name = DumpStrings.ReadFixed(data, record + 8, nameLength);
                table.Add(name, offsetBlocks * BlockSize, sizeBlocks * BlockSize);
            }

            host.Log(Strings.Format("Extract_PartsFromTable", table.Count), AnalysisLogLevel.Found);
        }

        private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
        {
            if (data.Length < signature.Length) return false;
            return data[..signature.Length].SequenceEqual(signature);
        }
    }

    /// <summary>
    /// Телевизоры на Novatek. Таблица помечена восьмибайтовой сигнатурой и лежит где-то
    /// в первой шестнадцатой части дампа — ищется перебором по границам в мегабайт.
    /// </summary>
    public sealed class NovatekDetector : ILayoutDetector
    {
        private const ulong Signature = 0xE7F435B9F9BC3947;
        private const long ScanStep = 0x100000;
        private const int TableLength = 0x800;
        private const int RecordsStart = 0x08;
        private const int RecordLength = 32;

        public string Name => "NOVATEK";

        public int Priority => 220;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            long limit = dump.LogicalSize / 16;

            for (long offset = ScanStep; offset <= limit; offset += ScanStep)
            {
                ct.ThrowIfCancellationRequested();

                if (!dump.Contains(offset, TableLength)) break;

                var table = dump.ReadBlock(offset, TableLength);
                if (BinaryPrimitives.ReadUInt64LittleEndian(table) != Signature) continue;

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

            for (int record = RecordsStart; record + RecordLength <= data.Length; record += RecordLength)
            {
                string name = DumpStrings.ReadFixed(data, record, 16);
                if (name.Length == 0) break;

                long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(record + 16));
                long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(record + 24));

                table.Add(name, offset, size);
            }

            host.Log(Strings.Format("Extract_PartsFromTable", table.Count), AnalysisLogLevel.Found);
        }
    }

    /// <summary>
    /// Спутниковые ресиверы. Таблица лежит в конце первого блока, а поля в ней записаны
    /// в обратном порядке байт — единственная разметка с такой особенностью.
    /// </summary>
    public sealed class SatReceiverDetector : ILayoutDetector
    {
        private const uint Signature = 0xFADEBCAA;
        private const int RecordLength = 0x18;
        private const int TableLength = 0x200;

        public string Name => "SAT RECEIVER";

        public int Priority => 270;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            long offset = TableOffset;
            if (!dump.Contains(offset, TableLength)) return null;

            var table = dump.ReadBlock(offset, TableLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(table) != Signature) return null;

            return new LayoutDetection(Name, offset, table);
        }

        /// <summary>
        /// Таблица занимает последний сектор перед началом второго блока.
        ///
        /// Оригинал считал этот адрес в физических координатах: <c>addSpare(0x20000)</c>
        /// минус spare, минус сектор. Для любой встречающейся геометрии это ровно то же
        /// место, что и логический <c>0x20000 - 0x200</c>: 0x20000 кратно размеру main,
        /// поэтому адрес попадает на границу страницы, а вычитание spare возвращает
        /// в область данных предыдущей страницы. Читает по логическому адресу сам
        /// читатель дампа, он же делает пересчёт.
        /// </summary>
        private const long TableOffset = 0x20000 - TableLength;

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            var data = detection.Table;
            int count = data[4];

            // Записи начинаются сразу за сигнатурой и байтом количества.
            const int RecordsStart = 5;

            for (int i = 0; i < count; i++)
            {
                int record = RecordsStart + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                string name = DumpStrings.ReadFixed(data, record, 8);

                // Big-endian: у этой прошивки числа записаны в обратном порядке байт.
                //
                // В таблице записаны логические адреса — сам оригинал называл прочитанные
                // значения «without_spare». Здесь они такими и остаются: в физические их
                // переведёт общий пересчёт перед записью на диск, как и у всех остальных
                // разметок. Оригинал же переводил их прямо тут, а потом общий пересчёт
                // переводил ещё раз — единственная разметка, где это делалось дважды.
                long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 0x08));
                long offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 0x10));

                table.Add(name, offset, length);
            }

            host.Log(Strings.Format("Extract_PartsFromTable", count), AnalysisLogLevel.Found);
        }
    }
}
