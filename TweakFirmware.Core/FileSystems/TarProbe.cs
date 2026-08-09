using System;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>
    /// Архив tar. Заголовка у самого архива нет — есть только заголовки файлов внутри,
    /// и опознаётся он тем, что контрольная сумма первого заголовка сходится.
    ///
    /// Дальше архив проходится по заголовкам: каждый говорит размер своего файла, а
    /// значит, и где искать следующий. Кончается архив там, где очередной заголовок
    /// перестаёт быть заголовком.
    /// </summary>
    public sealed class TarProbe : IFileSystemProbe
    {
        private const int BlockSize = 512;

        /// <summary>Поле контрольной суммы в заголовке.</summary>
        private const int ChecksumOffset = 0x94;

        private const int ChecksumLength = 8;

        /// <summary>
        /// Предел на число файлов в архиве. У оригинала его не было, и повреждённый архив,
        /// у которого размер файла читается как ноль, зацикливал разбор на месте.
        /// </summary>
        private const int MaxEntries = 200_000;

        public FsType Type => FsType.Tar;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            if (offset < 0 || offset + BlockSize > dump.LogicalSize) return null;

            var header = dump.ReadBlock(offset, BlockSize);
            if (!IsValidHeader(header)) return null;

            string name = ReadText(header, 0, 100);
            long at = offset;
            int files = 0;

            for (int i = 0; i < MaxEntries; i++)
            {
                if (at + BlockSize > dump.LogicalSize) break;

                var entry = dump.ReadBlock(at, BlockSize);
                if (!IsValidHeader(entry)) break;

                // Размер файла записан восьмеричным числом в виде текста.
                long fileSize = ReadOctal(entry, 124, 11);

                // Обычный файл помечен нулём; остальные типы — каталоги, ссылки и прочее.
                if (entry[0x9C] == (byte)'0') files++;

                long blocks = 1 + fileSize / BlockSize + (fileSize % BlockSize > 0 ? 1 : 0);
                at += blocks * BlockSize;
            }

            long length = at - offset;
            if (length <= BlockSize) return null;

            // В именах файлов бывают косые черты — в имени раздела они не нужны.
            string label = name.Replace('/', '_');
            if (label.Length == 0) label = "part";

            var volume = new VolumeInfo(FsType.Tar, label, offset, length)
            {
                Details = new[] { Strings.Format("Extract_TarVolume", name, $"0x{offset:X}", $"0x{length:X}", files) }
            };

            return ProbeHelpers.ClampToPartition(volume, partitionEnd);
        }

        /// <summary>
        /// Заголовок считается настоящим, если его контрольная сумма сходится. Считается
        /// она двумя способами — байты как беззнаковые и как знаковые, — потому что
        /// исторически архиваторы расходились в этом месте, и оба варианта встречаются.
        /// </summary>
        private static bool IsValidHeader(byte[] header)
        {
            if (header.Length < BlockSize) return false;

            // Первый байт имени: пустое имя означает конец архива, а не заголовок.
            if (header[0] < 0x2F || header[0] > 0x7A) return false;

            // Поля прав, владельца и размера записаны восьмеричными цифрами.
            int digits = 0;
            for (int i = 100; i < 156; i++)
            {
                byte b = header[i];
                bool octalDigit = b is >= (byte)'0' and <= (byte)'7';
                if (b != 0 && b != 0x20 && !octalDigit) return false;
                digits += b;
            }
            if (digits == 0) return false;

            uint unsignedSum = 0;
            int signedSum = 0;

            for (int i = 0; i < BlockSize; i++)
            {
                unsignedSum += header[i];
                signedSum += (sbyte)header[i];
            }

            // Само поле суммы при подсчёте считается заполненным пробелами.
            for (int i = 0; i < ChecksumLength; i++)
            {
                unsignedSum -= header[ChecksumOffset + i];
                signedSum -= (sbyte)header[ChecksumOffset + i];
            }

            unsignedSum += 32 * ChecksumLength;
            signedSum += 32 * ChecksumLength;

            long stored = ReadOctal(header, 148, 6);
            return stored == unsignedSum || stored == signedSum;
        }

        private static long ReadOctal(byte[] data, int at, int length)
        {
            long value = 0;
            for (int i = at; i < at + length && i < data.Length; i++)
            {
                byte b = data[i];
                if (b is < (byte)'0' or > (byte)'7') break;
                value = value * 8 + (b - '0');
            }
            return value;
        }

        private static string ReadText(byte[] data, int at, int maxLength)
        {
            int end = at;
            int limit = Math.Min(data.Length, at + maxLength);
            while (end < limit && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, at, end - at);
        }
    }
}
