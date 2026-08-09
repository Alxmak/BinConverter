using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>Разобранный суперблок ext4 — всё, что нужно и для показа, и для обхода каталогов.</summary>
    public sealed class Ext4Superblock
    {
        public long Offset { get; init; }
        public long BlockSize { get; init; }
        public uint BlockCount { get; init; }
        public uint BlocksFree { get; init; }
        public uint InodeCount { get; init; }
        public uint InodesFree { get; init; }
        public uint InodesPerGroup { get; init; }
        public ushort InodeSize { get; init; }
        public ushort State { get; init; }
        public ushort ErrorPolicy { get; init; }
        public string VolumeName { get; init; } = "";
        public string LastMountedPath { get; init; } = "";
        public ulong UuidHigh { get; init; }
        public ulong UuidLow { get; init; }

        public long VolumeSize => BlockCount * BlockSize;

        /// <summary>
        /// Имя тома: метка, если она есть, иначе последний путь монтирования. Когда нет
        /// ни того ни другого, раздел так и называется — «part».
        /// </summary>
        public string Name =>
            VolumeName.Length > 0 ? VolumeName :
            LastMountedPath.Length > 0 ? LastMountedPath : "part";
    }

    /// <summary>
    /// Опознание тома ext4 по суперблоку.
    ///
    /// Суперблок лежит не в начале тома, а со смещением 0x400 — первый килобайт
    /// зарезервирован под загрузочный код и в образах прошивок пуст. Пустота эта здесь
    /// не формальность, а часть проверки: без неё магическое число ext4 слишком часто
    /// находится в случайных данных.
    /// </summary>
    public sealed class Ext4Probe : IFileSystemProbe
    {
        private const int SuperblockOffset = 0x400;
        private const ushort Magic = 0xEF53;

        public FsType Type => FsType.Ext4;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            var superblock = TryReadSuperblock(dump, offset);
            if (superblock is null) return null;

            var volume = new VolumeInfo(FsType.Ext4, superblock.Name, offset, superblock.VolumeSize)
            {
                Details = Describe(superblock)
            };

            return ProbeHelpers.ClampToPartition(volume, partitionEnd);
        }

        /// <summary>
        /// Читает суперблок или возвращает <c>null</c>. Отдельный метод, потому что тем же
        /// суперблоком пользуется обход каталогов при поиске build.prop.
        /// </summary>
        public static Ext4Superblock? TryReadSuperblock(IDumpReader dump, long offset)
        {
            if (offset < 0 || offset + ProbeHelpers.BlockAlignment > dump.LogicalSize) return null;

            var head = dump.ReadBlock(offset, (int)ProbeHelpers.BlockAlignment);

            if (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x438)) != Magic) return null;
            if (!IsReservedAreaBlank(head)) return null;

            uint logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x418));
            uint logFragmentSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x41C));

            // Размер блока и размер фрагмента у ext4 всегда совпадают, а логарифм больше
            // шести не встречается. Оба условия отсекают случайные совпадения магии.
            if (logBlockSize != logFragmentSize || logBlockSize >= 7) return null;

            return new Ext4Superblock
            {
                Offset = offset,
                BlockSize = 0x400L << (int)logBlockSize,
                BlockCount = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x404)),
                BlocksFree = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x40C)),
                InodeCount = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x400)),
                InodesFree = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x410)),
                InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x428)),
                InodeSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x458)),
                State = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x43A)),
                ErrorPolicy = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0x43C)),
                VolumeName = LastPathSegment(ReadText(head, 0x478, 16)),
                LastMountedPath = LastPathSegment(ReadText(head, 0x488, 64)),
                UuidHigh = BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x468)),
                UuidLow = BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(0x470))
            };
        }

        /// <summary>
        /// Первый килобайт тома должен быть пустым. Проверка перенесена дословно, вместе
        /// со сравнением восьмибайтового слова с 0xFF: пустой областью здесь считаются
        /// нули, и от того, что стёртая область NAND под это не подходит, ничего не
        /// меняется — в образах ext4 первый килобайт именно обнулён. Смягчать условие
        /// нельзя: оно и отсеивает ложные срабатывания.
        /// </summary>
        private static bool IsReservedAreaBlank(ReadOnlySpan<byte> head)
        {
            for (int i = 0; i < SuperblockOffset; i += 8)
            {
                ulong word = BinaryPrimitives.ReadUInt64LittleEndian(head[i..]);
                if (word == 0 || word == 0xFF) continue;
                return false;
            }
            return true;
        }

        /// <summary>Из пути вида /system остаётся только последняя часть.</summary>
        private static string LastPathSegment(string text)
        {
            int slash = text.LastIndexOf('/');
            return slash >= 0 ? text[(slash + 1)..] : text;
        }

        private static string ReadText(byte[] data, int offset, int maxLength)
        {
            int end = offset;
            int limit = Math.Min(data.Length, offset + maxLength);
            while (end < limit && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static List<string> Describe(Ext4Superblock sb)
        {
            var lines = new List<string>
            {
                Strings.Format("Extract_Ext4Volume", sb.Name, $"0x{sb.Offset:X}", $"0x{sb.VolumeSize:X}"),
                Strings.Format("Extract_Ext4BlockSize", sb.BlockSize / 1024)
            };

            if (sb.BlockCount > 0)
                lines.Add(Strings.Format("Extract_Ext4Blocks", sb.BlockCount, sb.BlocksFree, sb.BlocksFree * 100 / sb.BlockCount));

            if (sb.InodeCount > 0)
                lines.Add(Strings.Format("Extract_Ext4Inodes", sb.InodeCount, sb.InodesFree, sb.InodesFree * 100 / sb.InodeCount));

            lines.Add(Strings.Format("Extract_Ext4State", StateText(sb.State)));

            if (sb.State == 2)
                lines.Add(Strings.Format("Extract_Ext4ErrorPolicy", ErrorPolicyText(sb.ErrorPolicy)));

            lines.Add(Strings.Format("Extract_Ext4Uuid", $"{sb.UuidHigh:X16}{sb.UuidLow:X16}"));

            if (sb.LastMountedPath.Length > 0)
                lines.Add(Strings.Format("Extract_Ext4LastMounted", sb.LastMountedPath));

            return lines;
        }

        private static string StateText(ushort state) => state switch
        {
            1 => Strings.Get("Extract_Ext4StateClean"),
            2 => Strings.Get("Extract_Ext4StateErrors"),
            4 => Strings.Get("Extract_Ext4StateOrphans"),
            _ => Strings.Get("Extract_Ext4StateUnknown")
        };

        private static string ErrorPolicyText(ushort policy) => policy switch
        {
            1 => Strings.Get("Extract_Ext4PolicyContinue"),
            2 => Strings.Get("Extract_Ext4PolicyReadOnly"),
            3 => Strings.Get("Extract_Ext4PolicyPanic"),
            _ => Strings.Get("Extract_Ext4StateUnknown")
        };
    }
}
