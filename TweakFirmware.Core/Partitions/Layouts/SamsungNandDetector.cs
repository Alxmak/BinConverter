using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Телевизоры Samsung с памятью NAND.
    ///
    /// Разметка устроена наоборот по сравнению с остальными: служебные метки лежат не в
    /// начале дампа, а в самом конце, поэтому и поиск идёт с конца. Меток две — LPCH и
    /// UPCH, — и по расстоянию между ними определяется размер блока микросхемы, который
    /// иначе взять неоткуда: все смещения и размеры в таблице заданы в блоках.
    /// </summary>
    public sealed class SamsungNandDetector : ILayoutDetector
    {
        /// <summary>Строка LPCH, прочитанная как 32-битное число.</summary>
        private const uint LpchMarker = 0x4843504C;

        /// <summary>Строка UPCH.</summary>
        private const uint UpchMarker = 0x48435055;

        private const int RecordLength = 0x10;

        private long _lpch;
        private long _upch;

        public string Name => "SAMSUNG NAND";

        public int Priority => 300;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            if (context.Geometry is not { } geometry) return null;

            _lpch = 0;
            _upch = 0;

            long pages = dump.PhysicalSize / geometry.Page;
            long offset = dump.LogicalSize - geometry.Main;

            for (long i = 0; i < pages && offset >= 0 && (_lpch == 0 || _upch == 0); i++, offset -= geometry.Main)
            {
                ct.ThrowIfCancellationRequested();

                if ((i & 0xFF) == 0)
                    host.Progress?.Report(new AnalysisProgress(Name, i, pages));

                var head = dump.ReadBlock(offset, 4);
                uint marker = BinaryPrimitives.ReadUInt32LittleEndian(head);

                if (marker == LpchMarker) _lpch = offset;
                else if (marker == UpchMarker) _upch = offset;
            }

            if (_lpch == 0 || _upch == 0) return null;

            return LayoutDetection.At(Name, _lpch);
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            if (context.Geometry is not { } geometry) return;

            long blockSize = Math.Abs(_lpch - _upch);
            if (blockSize <= 0) return;

            context.NandBlockSize = blockSize;

            host.Log(Strings.Format("Extract_SamsungMarkers", $"0x{_upch:X}", $"0x{_lpch:X}"), AnalysisLogLevel.Found);
            host.Log(Strings.Format("Extract_NandBlockSize", dump.LogicalSize / blockSize, $"0x{blockSize:X}"));

            // Таблица разделов лежит рядом с меткой LPCH; сколько именно читать, заранее
            // неизвестно, поэтому берём блок целиком.
            int length = (int)Math.Min(blockSize, dump.LogicalSize - _lpch);
            if (length <= 0) return;

            var data = dump.ReadBlock(_lpch, length);

            // Признак начала записей. Если его нет, оригинал брал фиксированное смещение —
            // оно верно для большинства дампов этого семейства.
            int start = DumpStrings.IndexOf(data, "FSRPART");
            if (start < 0) start = 0x200;
            if (start + 0x10 > data.Length) return;

            int count = data[start + 0x0C];

            for (int i = 0; i < count; i++)
            {
                int record = start + 0x10 + i * RecordLength;
                if (record + RecordLength > data.Length) break;

                byte type = data[record];
                long offset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(record + 4)) * blockSize;
                long size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(record + 6)) * blockSize;

                // Своих имён у разделов здесь нет, поэтому имя собирается из номера
                // и типа — так же, как в оригинале.
                table.Add($"part_{i:D2}_{type:X2}", offset, size);
            }

            ReadBadBlockTable(dump, context, geometry, host);
        }

        /// <summary>
        /// Таблица плохих блоков лежит сразу за меткой UPCH и представляет собой пары
        /// шестнадцатибитных номеров, оборванные значением 0xFFFF.
        /// </summary>
        private void ReadBadBlockTable(IDumpReader dump, DumpContext context, NandGeometry geometry, IAnalysisHost host)
        {
            long offset = _upch + geometry.Main;
            int length = (int)Math.Min(geometry.Main, dump.LogicalSize - offset);
            if (length <= 8) return;

            var data = dump.ReadBlock(offset, length);

            // Первые четыре байта служебные — записи начинаются со второй пары.
            for (int i = 1; ; i++)
            {
                int at = i * 4;
                if (at + 4 > data.Length) break;

                ushort from = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
                ushort to = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));

                if (to == 0xFFFF) break;

                context.BadBlocks.Add(from);
            }

            if (context.BadBlocks.Count > 0)
                host.Log(Strings.Format("Extract_BadBlocksFound", context.BadBlocks.Count));
        }
    }
}
