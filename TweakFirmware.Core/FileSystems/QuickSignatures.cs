using System;
using System.Buffers.Binary;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>
    /// Быстрые проверки «лежит ли здесь начало такого-то тома».
    ///
    /// От полноценных зондов файловых систем отличаются тем, что не читают ни имени, ни
    /// размера — только отвечают «да» или «нет» по одному-двум байтам сигнатуры. Нужны
    /// там, где по дампу ищут опорные точки: у Sony разметки нет вовсе, и границы
    /// разделов восстанавливаются по тому, где в дампе встречаются суперблоки.
    /// </summary>
    public static class QuickSignatures
    {
        /// <summary>Суперблок ext4 лежит со смещением 0x400 от начала тома.</summary>
        public const int Ext4SuperblockOffset = 0x400;

        private const ushort Ext4Magic = 0xEF53;

        /// <summary>Начало тома ext4: магическое число в суперблоке.</summary>
        public static bool IsExt4(IDumpReader dump, long offset)
        {
            if (offset < 0 || offset + 0x800 >= dump.LogicalSize) return false;

            var superblock = dump.ReadBlock(offset + Ext4SuperblockOffset, 0x400);
            return BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(0x38)) == Ext4Magic;
        }

        /// <summary>
        /// Начало тома FAT16. Метка типа лежит в одном из двух мест — в зависимости от
        /// того, каким инструментом том создавали.
        /// </summary>
        public static bool IsFat16(IDumpReader dump, long offset)
        {
            if (offset < 0 || offset + 0x60 >= dump.LogicalSize) return false;

            var head = dump.ReadBlock(offset, 0x60);
            return Matches(head, 0x36, "FAT16") || Matches(head, 0x52, "FAT16");
        }

        /// <summary>Заголовок Android Verified Boot.</summary>
        public static bool IsAvb(IDumpReader dump, long offset)
        {
            if (offset < 0 || offset + 0x10 >= dump.LogicalSize) return false;

            var head = dump.ReadBlock(offset, 0x10);
            return Matches(head, 0, "AVB0");
        }

        /// <summary>
        /// Размер тома ext4 по его суперблоку. Если суперблока нет или числа в нём
        /// бессмысленны, считается, что том занимает всё до конца дампа, — так поступал
        /// и оригинал, и для последнего раздела это как раз верно.
        /// </summary>
        public static long Ext4Size(IDumpReader dump, long offset)
        {
            long toEnd = dump.LogicalSize - offset;
            if (!IsExt4(dump, offset)) return toEnd;

            var superblock = dump.ReadBlock(offset + Ext4SuperblockOffset, 0x400);

            uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x04));
            uint logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x18));

            // Размер блока записан логарифмом: 0 — килобайт, 1 — два, и так далее.
            // Значения больше восьми в природе не встречаются и означают мусор.
            if (logBlockSize > 8) return toEnd;

            long blockSize = 0x400L << (int)logBlockSize;
            long size = blockCount * blockSize;

            if (size == 0 || offset + size > dump.LogicalSize) return toEnd;
            return size;
        }

        private static bool Matches(ReadOnlySpan<byte> data, int offset, string text)
        {
            if (offset + text.Length > data.Length) return false;

            for (int i = 0; i < text.Length; i++)
                if (data[offset + i] != (byte)text[i]) return false;

            return true;
        }
    }
}
