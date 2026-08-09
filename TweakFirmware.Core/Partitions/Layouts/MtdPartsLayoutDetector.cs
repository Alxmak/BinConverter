using System;
using System.Collections.Generic;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Общее основание для разметок, которые описаны строкой параметров ядра.
    ///
    /// Таких восемь из двадцати пяти, и различаются они только тремя вещами: где в дампе
    /// искать блок конфигурации, как убедиться, что это он, и по какой подстроке узнать
    /// список разделов. Сам разбор строки одинаков для всех и живёт в
    /// <see cref="MtdPartsParser"/>.
    ///
    /// Блок читается в буфер, который переиспользуется между попытками: у некоторых
    /// разметок кандидатов тысячи, и выделять на каждую по сто килобайт значило бы
    /// сгенерировать гигабайты мусора на один разбор.
    /// </summary>
    public abstract class MtdPartsLayoutDetector : ILayoutDetector
    {
        private byte[]? _buffer;

        public abstract string Name { get; }

        public abstract int Priority { get; }

        /// <summary>Подстрока, по которой узнаётся список разделов.</summary>
        protected abstract string Marker { get; }

        /// <summary>Сколько байт читать в каждом кандидате.</summary>
        protected abstract int BlockLength { get; }

        /// <summary>Адреса, по которым имеет смысл искать блок конфигурации.</summary>
        protected abstract IEnumerable<long> Candidates(IDumpReader dump, DumpContext context);

        /// <summary>
        /// Проверка блока до поиска строки. По умолчанию блок принимается любой: большинству
        /// разметок хватает того, что в нём нашлась подстрока-маркер.
        /// </summary>
        protected virtual bool IsBlockValid(ReadOnlySpan<byte> block) => true;

        /// <summary>Показывать ли прогресс перебора. Нужно только тем, у кого кандидатов много.</summary>
        protected virtual bool ReportsProgress => false;

        /// <summary>Сколько всего кандидатов будет перебрано — для полосы прогресса.</summary>
        protected virtual long ProgressTotal(IDumpReader dump) => 0;

        /// <summary>
        /// Доводка разделов после разбора строки. Нужна там, где размеры в строке заданы
        /// не в байтах: у Rockchip, например, в секторах.
        /// </summary>
        protected virtual void PostProcess(PartitionTable table, int addedFrom) { }

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            long done = 0;
            long total = ReportsProgress ? ProgressTotal(dump) : 0;

            foreach (long offset in Candidates(dump, context))
            {
                ct.ThrowIfCancellationRequested();

                if (ReportsProgress)
                {
                    done++;
                    if ((done & 0x3F) == 0)
                        host.Progress?.Report(new AnalysisProgress(Name, done, total));
                }

                // Адрес за концом дампа — это пропуск одного кандидата, а не конец перебора:
                // у разметок с несколькими известными адресами часть их заведомо больше
                // маленького дампа, а нужный может оказаться следующим в списке.
                if (offset < 0 || offset >= dump.LogicalSize) continue;

                // У конца дампа читаем сколько есть: детекторы щупают адреса вслепую,
                // и упираться в границу здесь — обычное дело, а не ошибка.
                int length = (int)Math.Min(BlockLength, dump.LogicalSize - offset);
                if (length <= 0) continue;

                var buffer = RentBuffer();
                var block = buffer.AsSpan(0, length);
                dump.Read(offset, block);

                if (!IsBlockValid(block)) continue;
                if (DumpStrings.IndexOf(block, Marker, length) < 0) continue;

                return new LayoutDetection(Name, offset, block.ToArray());
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
            int position = DumpStrings.IndexOf(detection.Table, Marker);
            if (position < 0) return;

            string text = DumpStrings.ReadUntilNulOrSpace(detection.Table, position);
            host.Log(Strings.Format("Extract_PartsStringFound", $"0x{detection.TableOffset:X}"), AnalysisLogLevel.Found);
            host.Log(text);

            int before = table.Count;
            var parsed = MtdPartsParser.Parse(text);

            foreach (var part in parsed.Parts) table.Add(part);
            foreach (string rejected in parsed.Rejected)
                host.Log(Strings.Format("Extract_PartsStringRecordRejected", rejected), AnalysisLogLevel.Warning);

            PostProcess(table, before);
        }

        private byte[] RentBuffer() => _buffer ??= new byte[BlockLength];

        /// <summary>
        /// Блок конфигурации, подписанный контрольной суммой: первые четыре байта — CRC32
        /// всего остального. Так устроены блоки у MTK, HiSilicon, MSTAR и NOR-микросхем,
        /// и именно по сумме они отличаются от случайно похожих данных.
        /// </summary>
        protected static bool HasValidCrc32(ReadOnlySpan<byte> block, int length)
        {
            if (length > block.Length || length <= 4) return false;
            return Crc32.CheckLeadingChecksum(block[..length]);
        }
    }
}
