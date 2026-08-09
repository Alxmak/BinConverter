using System;
using System.Collections.Generic;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Телевизоры на MSTAR с памятью NAND.
    ///
    /// Единственная разметка, у которой блок конфигурации не привязан к фиксированному
    /// адресу: его приходится искать перебором с шагом в четыре килобайта по диапазону
    /// от двух до шестидесяти четырёх мегабайт. Чтобы перебор не занимал вечность,
    /// проверка идёт в два этапа — сначала дешёвая примета, потом контрольная сумма.
    ///
    /// Примета простая: блок конфигурации — это текст вида «ключ=значение», поэтому в
    /// первых ста двадцати восьми байтах знак равенства встречается несколько раз.
    /// Случайные данные такому условию почти никогда не удовлетворяют, а те, что
    /// удовлетворили, отсеиваются контрольной суммой.
    /// </summary>
    public sealed class MstarNandDetector : MtdPartsLayoutDetector
    {
        private const long ScanStart = 0x200000;
        private const long ScanEnd = 0x4000000;
        private const long ScanStep = 0x1000;

        /// <summary>
        /// Длины, на которых проверяется контрольная сумма: 16, 32, 64 и 128 килобайт.
        /// Какая именно у конкретного дампа — заранее неизвестно, поэтому перебираются все.
        /// </summary>
        private static readonly int[] ChecksummedLengths = { 65536, 16384, 32768, 131072 };

        public override string Name => "MSTAR NAND";

        public override int Priority => 280;

        /// <summary>
        /// Ищется хвост имени устройства: у разных моделей оно своё
        /// (mt53xx-nand, edb64M-nand), а общее у них — окончание.
        /// </summary>
        protected override string Marker => "-nand:";

        protected override int BlockLength => 0x20000;

        protected override bool ReportsProgress => true;

        protected override long ProgressTotal(IDumpReader dump) =>
            (Math.Min(ScanEnd, dump.LogicalSize) - ScanStart) / ScanStep;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            for (long offset = ScanStart; offset <= ScanEnd; offset += ScanStep)
            {
                if (offset >= dump.LogicalSize) yield break;
                yield return offset;
            }
        }

        protected override bool IsBlockValid(ReadOnlySpan<byte> block)
        {
            if (!LooksLikeKeyValueText(block)) return false;

            foreach (int length in ChecksummedLengths)
            {
                if (length <= block.Length && HasValidCrc32(block, length)) return true;
            }

            return false;
        }

        /// <summary>Несколько знаков равенства в начале блока — признак текста конфигурации.</summary>
        private static bool LooksLikeKeyValueText(ReadOnlySpan<byte> block)
        {
            const int Start = 4;
            const int End = 0x80;
            const int Needed = 4;

            if (block.Length < End) return false;

            int count = 0;
            for (int i = Start; i < End; i++)
            {
                if (block[i] == (byte)'=' && ++count >= Needed) return true;
            }

            return false;
        }
    }
}
