using System;
using System.Collections.Generic;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Android-приставки и телевизоры на MediaTek: блок конфигурации лежит по одному из
    /// пяти известных адресов и подписан контрольной суммой.
    /// </summary>
    public sealed class MtkAndroidDetector : MtdPartsLayoutDetector
    {
        private static readonly long[] KnownOffsets =
        {
            0x00200000, 0x00480000, 0x1D800000, 0x00900000, 0x00400000
        };

        public override string Name => "MTK ANDROID";

        public override int Priority => 70;

        protected override string Marker => "mtdparts=";

        protected override int BlockLength => 0x20000;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context) => KnownOffsets;

        protected override bool IsBlockValid(ReadOnlySpan<byte> block) => HasValidCrc32(block, BlockLength);
    }

    /// <summary>Спутниковые Android-приставки: разделы описаны в терминах блочного устройства eMMC.</summary>
    public sealed class ReceiverAndroidDetector : MtdPartsLayoutDetector
    {
        public override string Name => "RECEIVER ANDROID";

        public override int Priority => 110;

        protected override string Marker => "blkdevparts=mmcblk0:";

        protected override int BlockLength => 0x10000;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            yield return 0x100000;
        }

        protected override bool IsBlockValid(ReadOnlySpan<byte> block) => HasValidCrc32(block, BlockLength);
    }

    /// <summary>
    /// Микросхемы NOR, подключённые по SPI. Блок конфигурации может лежать где угодно,
    /// поэтому проверяется каждый блок в 64 КиБ — благо весь дамп не больше 16 МиБ.
    /// </summary>
    public sealed class NorFlashDetector : MtdPartsLayoutDetector
    {
        /// <summary>Размеры дампов, при которых вообще имеет смысл искать: 4, 8 и 16 МиБ.</summary>
        private static readonly long[] Sizes = { 0x400000, 0x800000, 0x1000000 };

        public override string Name => "NOR FLASH";

        public override int Priority => 30;

        protected override string Marker => "mtdparts=nor-flash:";

        protected override int BlockLength => 0x10000;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            if (Array.IndexOf(Sizes, dump.LogicalSize) < 0) yield break;

            for (long offset = 0; offset < dump.LogicalSize; offset += BlockLength)
                yield return offset;
        }

        protected override bool IsBlockValid(ReadOnlySpan<byte> block) => HasValidCrc32(block, BlockLength);
    }

    /// <summary>Телевизионные приставки на Rockchip PX3: блок помечен меткой PARM.</summary>
    public sealed class RockchipPx3Detector : MtdPartsLayoutDetector
    {
        public override string Name => "ROCKCHIP PX3";

        public override int Priority => 100;

        protected override string Marker => "mtdparts=rk29xxnand:";

        protected override int BlockLength => 0x100000;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            for (long offset = 0x400000; offset <= 0x600000; offset += 0x100000)
                yield return offset;
        }

        protected override bool IsBlockValid(ReadOnlySpan<byte> block) =>
            block.Length >= 4 && block[0] == (byte)'P' && block[1] == (byte)'A' && block[2] == (byte)'R' && block[3] == (byte)'M';

        /// <summary>
        /// У Rockchip размеры и смещения в строке заданы не в байтах, а в секторах —
        /// единственная разметка с такой особенностью.
        /// </summary>
        protected override void PostProcess(PartitionTable table, int addedFrom)
        {
            const long SectorSize = 0x200;

            for (int i = addedFrom; i < table.Count; i++)
            {
                var part = table[i];
                part.Offset *= SectorSize;

                // Открытый конец не трогаем: настоящий размер подставится позже.
                if (part.Length > 0) part.Length *= SectorSize;
            }
        }
    }

    /// <summary>Телевизоры на HiSilicon с памятью NAND.</summary>
    public sealed class HiSiliconNandDetector : MtdPartsLayoutDetector
    {
        public override string Name => "HISILICON NAND";

        public override int Priority => 250;

        protected override string Marker => "mtdparts=hinand:";

        protected override int BlockLength => 0x20000;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            yield return 0x80000;
        }

        /// <summary>Подписан не весь блок, а только первые четыре килобайта.</summary>
        protected override bool IsBlockValid(ReadOnlySpan<byte> block) => HasValidCrc32(block, 4096);
    }

    /// <summary>Телевизоры на Novatek с памятью NAND — шасси вроде 17MB211.</summary>
    public sealed class NvtNandDetector : MtdPartsLayoutDetector
    {
        public override string Name => "NVT NAND";

        public override int Priority => 240;

        protected override string Marker => "mtdparts=nvt_nand:";

        protected override int BlockLength => 0x20000;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            yield return 0x700000;
        }
    }

    /// <summary>
    /// Телевизоры на MediaTek с памятью NAND. Отличаются от прочих тем, что сначала
    /// проверяется метка загрузчика в начале дампа, и только потом читается таблица.
    /// </summary>
    public sealed class MediatekNandDetector : MtdPartsLayoutDetector
    {
        private const string BootMarker = "MTK/DTV/ROMCODE/NANDBOOT/V";
        private const long BootMarkerOffset = 0x100;
        private const long TableOffset = 0x200000;

        public override string Name => "MEDIATEK NAND";

        public override int Priority => 290;

        protected override string Marker => "mtdparts=mt53xx-nand:";

        /// <summary>
        /// Строка ищется только в начале блока. Ограничение из оригинала: дальше начинается
        /// уже не конфигурация, и совпадение там было бы случайным.
        /// </summary>
        protected override int BlockLength => 0x400;

        protected override IEnumerable<long> Candidates(IDumpReader dump, DumpContext context)
        {
            // Метка загрузчика в первой странице — признак того, что дамп вообще от MediaTek.
            var head = dump.ReadBlock(BootMarkerOffset, BootMarker.Length);
            if (DumpStrings.IndexOf(head, BootMarker) != 0) yield break;

            yield return TableOffset;
        }
    }
}
