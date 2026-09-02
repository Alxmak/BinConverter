using System;
using System.Buffers.Binary;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>
    /// SquashFS — сжатый образ только для чтения, самая частая корневая файловая система
    /// в прошивках телевизоров.
    ///
    /// Читаются все четыре версии формата, причём поля у них лежат по разным адресам,
    /// и вдобавок образ встречается в обоих порядках байт: часть процессоров работает
    /// с прошивкой как есть, а часть требует перевёрнутую.
    /// </summary>
    public sealed class SquashFsProbe : IFileSystemProbe
    {
        private const uint Magic = 0x73717368;
        private const uint MagicSwapped = 0x68737173;

        public FsType Type => FsType.SquashFs;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            if (offset < 0 || offset + 0x100 > dump.LogicalSize) return null;

            var head = dump.ReadBlock(offset, 0x100);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);

            bool swapped = magic == MagicSwapped;
            if (magic != Magic && !swapped) return null;

            ushort major = Read16(head, 28, swapped);
            ushort minor = Read16(head, 30, swapped);
            if (major is < 1 or > 4) return null;

            long bytesUsed;
            uint blockSize;
            ushort blockLog;
            ushort compression = 0;

            switch (major)
            {
                case 4:
                    blockSize = Read32(head, 12, swapped);
                    compression = Read16(head, 20, swapped);
                    blockLog = Read16(head, 22, swapped);
                    bytesUsed = (long)Read64(head, 40, swapped);
                    break;

                case 3:
                    blockLog = Read16(head, 34, swapped);
                    blockSize = Read32Unaligned(head, 51, swapped);
                    bytesUsed = (long)Read64Unaligned(head, 63, swapped);
                    break;

                case 2:
                    blockLog = Read16(head, 34, swapped);
                    blockSize = Read32Unaligned(head, 51, swapped);
                    bytesUsed = Read32(head, 8, swapped);
                    break;

                default:
                    blockLog = Read16(head, 34, swapped);
                    blockSize = Read16(head, 32, swapped);
                    bytesUsed = Read32(head, 8, swapped);
                    break;
            }

            if (bytesUsed <= 0) return null;

            string comment = "";

            // Размер блока и его логарифм обязаны сходиться — иначе образ повреждён.
            // Том всё равно добавляется: извлечь его может быть полезно и таким.
            if (blockSize > 0 && BitLength(blockSize) - 1 != blockLog)
                comment = Strings.Get("Extract_SquashFsCorrupt");

            long length = ProbeHelpers.AlignUp(bytesUsed, ProbeHelpers.BlockAlignment);

            var volume = new VolumeInfo(FsType.SquashFs, "part", offset, length)
            {
                Comment = comment,
                Details = new[]
                {
                    Strings.Format("Extract_SquashFsVolume", $"{major}.{minor}",
                        swapped ? Strings.Get("Extract_SquashFsSwapped") : "",
                        $"0x{offset:X}", $"0x{length:X}"),
                    Strings.Format("Extract_BlockSize", blockSize / 1024),
                    major == 4 ? Strings.Format("Extract_SquashFsCompression", CompressionName(compression)) : ""
                }
            };

            return ProbeHelpers.ClampToPartition(volume, partitionEnd);
        }

        private static string CompressionName(ushort id) => id switch
        {
            1 => "GZIP",
            2 => "LZMA",
            3 => "LZO",
            4 => "XZ",
            5 => "LZ4",
            6 => "ZSTD",
            _ => "?"
        };

        private static int BitLength(uint value)
        {
            int bits = 0;
            while (value > 0)
            {
                value >>= 1;
                bits++;
            }
            return bits;
        }

        private static ushort Read16(byte[] d, int at, bool swapped) =>
            swapped
                ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at))
                : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));

        private static uint Read32(byte[] d, int at, bool swapped) =>
            swapped
                ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at))
                : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));

        private static ulong Read64(byte[] d, int at, bool swapped) =>
            swapped
                ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(at))
                : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at));

        // У версий 2 и 3 часть полей лежит по нечётным адресам — оригинал читал их
        // побайтно именно поэтому.
        private static uint Read32Unaligned(byte[] d, int at, bool swapped) => Read32(d, at, swapped);

        private static ulong Read64Unaligned(byte[] d, int at, bool swapped) => Read64(d, at, swapped);
    }

    /// <summary>
    /// ROMFS — простейший образ только для чтения. Все числа в нём записаны в обратном
    /// порядке байт, это часть формата.
    /// </summary>
    public sealed class RomFsProbe : IFileSystemProbe
    {
        private const string Signature = "-rom1fs-";

        public FsType Type => FsType.RomFs;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            if (offset < 0 || offset + 0x30 > dump.LogicalSize) return null;

            var head = dump.ReadBlock(offset, 0x30);
            if (System.Text.Encoding.ASCII.GetString(head, 0, 8) != Signature) return null;

            uint fullSize = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
            if (fullSize == 0) return null;

            string name = ReadName(head, 16, 16);
            long length = ProbeHelpers.AlignUp(fullSize, ProbeHelpers.BlockAlignment);

            var volume = new VolumeInfo(FsType.RomFs, name.Length > 0 ? name : "part", offset, length)
            {
                Details = new[] { Strings.Format("Extract_RomFsVolume", name, $"0x{offset:X}", $"0x{length:X}") }
            };

            return ProbeHelpers.ClampToPartition(volume, partitionEnd);
        }

        private static string ReadName(byte[] data, int at, int maxLength)
        {
            int end = at;
            int limit = Math.Min(data.Length, at + maxLength);
            while (end < limit && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, at, end - at);
        }
    }

    /// <summary>
    /// CRAMFS — ещё один сжатый образ только для чтения. Встречается в двух вариантах,
    /// которые различаются тем, есть ли перед суперблоком двухкилобайтный отступ.
    /// </summary>
    public sealed class CramFsProbe : IFileSystemProbe
    {
        private const uint Magic = 0x28CD3D45;

        /// <summary>Суперблок лежит либо в начале тома, либо через 0x800 байт.</summary>
        private static readonly int[] SuperblockOffsets = { 0, 0x800 };

        public FsType Type => FsType.CramFs;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            if (offset < 0 || offset + ProbeHelpers.BlockAlignment > dump.LogicalSize) return null;

            var head = dump.ReadBlock(offset, (int)ProbeHelpers.BlockAlignment);

            foreach (int at in SuperblockOffsets)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(at)) != Magic) continue;

                uint bytesUsed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(at + 4));
                if (bytesUsed == 0) continue;

                string name = ReadName(head, at + 48, 16);
                long start = offset + at;
                long length = ProbeHelpers.AlignUp(bytesUsed, ProbeHelpers.BlockAlignment);

                var volume = new VolumeInfo(FsType.CramFs, name.Length > 0 ? name : "part", start, length)
                {
                    Details = new[] { Strings.Format("Extract_CramFsVolume", name, $"0x{start:X}", $"0x{length:X}") }
                };

                return ProbeHelpers.ClampToPartition(volume, partitionEnd);
            }

            return null;
        }

        private static string ReadName(byte[] data, int at, int maxLength)
        {
            if (at >= data.Length) return "";
            int end = at;
            int limit = Math.Min(data.Length, at + maxLength);
            while (end < limit && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, at, end - at);
        }
    }
}
