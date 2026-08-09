using System;
using System.Buffers.Binary;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>
    /// VDFS — файловая система Samsung, которой размечены разделы в телевизорах этой марки.
    ///
    /// Опознаётся по двойной сигнатуре: одна в начале тома, вторая через 0x200 байт.
    /// Встречается в трёх видах — ранняя версия разметки, поздняя и предшественница
    /// под именем eMMCFS; поля у всех лежат по разным адресам.
    /// </summary>
    public sealed class VdfsProbe : IFileSystemProbe
    {
        /// <summary>Строка «VDFS» как 32-битное значение.</summary>
        private const uint Signature = 0x53464456;

        /// <summary>Восьмибайтовая сигнатура предшественницы — «1eMMCFS».</summary>
        private const ulong EmmcFsSignature = 0x015346434D4D6531;

        /// <summary>Версия разметки «0001» — у неё своё расположение полей.</summary>
        private const uint LayoutVersion0001 = 0x31303030;

        private const int SecondSignatureOffset = 0x200;

        public FsType Type => FsType.Vdfs;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            if (offset < 0 || offset + ProbeHelpers.BlockAlignment > dump.LogicalSize) return null;

            var head = dump.ReadBlock(offset, (int)ProbeHelpers.BlockAlignment);

            var volume = TryVdfs(head, offset) ?? TryEmmcFs(head, offset);
            return volume is null ? null : ProbeHelpers.ClampToPartition(volume, partitionEnd);
        }

        private static VolumeInfo? TryVdfs(byte[] head, long offset)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Signature) return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(SecondSignatureOffset)) != Signature) return null;

            uint layout = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x204));

            long size;
            int blockSize;
            string name;
            string driver;

            if (layout == LayoutVersion0001)
            {
                blockSize = 1 << head[0x209];
                int lebSize = 1 << head[0x20A];

                name = ReadText(head, 0x237, 32);
                driver = ReadText(head, 0x255, 32);
                size = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x20B)) * lebSize;
            }
            else
            {
                blockSize = 1 << head[0x2A4];

                name = ReadText(head, 0x22C, 16);
                driver = ReadText(head, 0x23C, 32);
                size = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x208)) * blockSize;
            }

            if (size <= 0) return null;

            // К имени тома дописывается версия разметки: в дампе рядом могут лежать
            // разделы разных версий, и по имени файла их иначе не различить. Версия
            // записана четырьмя печатными символами — оригинал приписывал к имени само
            // число, хотя в комментарии рядом с проверкой указана именно строка «0001».
            string label = $"{name}.vdfs{LayoutText(head, 0x204)}";

            return new VolumeInfo(FsType.Vdfs, label, offset, size)
            {
                Details = new[]
                {
                    Strings.Format("Extract_VdfsVolume", label, $"0x{offset:X}", $"0x{size:X}"),
                    Strings.Format("Extract_VdfsDriver", driver, blockSize / 1024)
                }
            };
        }

        private static VolumeInfo? TryEmmcFs(byte[] head, long offset)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(head) != EmmcFsSignature) return null;
            if (BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(SecondSignatureOffset)) != EmmcFsSignature) return null;

            int blockSize = 1 << head[0x208];
            int lebSize = 1 << head[0x209];

            string name = ReadText(head, 0x236, 32);
            string driver = ReadText(head, 0x254, 32);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x20A)) * lebSize;

            if (size <= 0) return null;

            return new VolumeInfo(FsType.Vdfs, name.Length > 0 ? name : "part", offset, size)
            {
                Details = new[]
                {
                    Strings.Format("Extract_VdfsVolume", name, $"0x{offset:X}", $"0x{size:X}"),
                    Strings.Format("Extract_VdfsDriver", driver, blockSize / 1024)
                }
            };
        }

        /// <summary>
        /// Версия разметки как текст. Если байты непечатные — а это уже не то, что автор
        /// закладывал, — остаётся шестнадцатеричная запись, лишь бы имя тома оставалось
        /// пригодным для имени файла.
        /// </summary>
        private static string LayoutText(byte[] data, int at)
        {
            for (int i = at; i < at + 4; i++)
                if (data[i] < 0x20 || data[i] > 0x7E)
                    return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)).ToString("X8");

            return System.Text.Encoding.ASCII.GetString(data, at, 4);
        }

        private static string ReadText(byte[] data, int at, int maxLength)
        {
            if (at >= data.Length) return "";
            int end = at;
            int limit = Math.Min(data.Length, at + maxLength);
            while (end < limit && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, at, end - at);
        }
    }
}
