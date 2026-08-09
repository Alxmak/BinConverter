using System;
using System.Text;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Поиск и чтение текста внутри двоичного блока дампа.
    ///
    /// Отдельно потому, что правило конца строки здесь необычное и одинаковое для всех
    /// детекторов: строка кончается не только нулевым байтом, но и пробелом. Так сделано
    /// в оригинале, и это не мелочь — параметры ядра записаны в дампе одной длинной
    /// строкой через пробел, и без остановки на пробеле в список разделов попадали бы
    /// соседние параметры.
    /// </summary>
    internal static class DumpStrings
    {
        /// <summary>Читает строку с заданного места до нулевого байта или пробела.</summary>
        public static string ReadUntilNulOrSpace(ReadOnlySpan<byte> buffer, int offset)
        {
            if (offset < 0 || offset >= buffer.Length) return "";

            int end = offset;
            while (end < buffer.Length && buffer[end] != 0x00 && buffer[end] != 0x20) end++;

            return Encoding.ASCII.GetString(buffer[offset..end]);
        }

        /// <summary>Читает строку фиксированной длины, обрывая её на первом нулевом байте.</summary>
        public static string ReadFixed(ReadOnlySpan<byte> buffer, int offset, int maxLength)
        {
            if (offset < 0 || offset >= buffer.Length) return "";

            int end = offset;
            int limit = Math.Min(buffer.Length, offset + maxLength);
            while (end < limit && buffer[end] != 0x00) end++;

            return Encoding.ASCII.GetString(buffer[offset..end]);
        }

        /// <summary>Ищет ASCII-подстроку в блоке. Возвращает смещение или -1.</summary>
        public static int IndexOf(ReadOnlySpan<byte> buffer, string needle, int limit = -1)
        {
            if (needle.Length == 0) return -1;

            int end = limit < 0 ? buffer.Length : Math.Min(limit, buffer.Length);
            end -= needle.Length;

            for (int start = 0; start <= end; start++)
            {
                int i = 0;
                while (i < needle.Length && buffer[start + i] == (byte)needle[i]) i++;
                if (i == needle.Length) return start;
            }

            return -1;
        }
    }
}
