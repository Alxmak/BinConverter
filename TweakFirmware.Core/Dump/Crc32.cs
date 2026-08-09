using System;

namespace TweakFirmware.Core.Dump
{
    /// <summary>
    /// CRC32 в варианте ISO-HDLC (он же zlib, он же «стандартный»): полином 0xEDB88320,
    /// начальное значение и завершающая инверсия 0xFFFFFFFF.
    ///
    /// Нужен в двух ролях. Во-первых, им подписан заголовок и таблица разделов GPT —
    /// это часть спецификации. Во-вторых, добрая половина детекторов разметок ищет свою
    /// таблицу не по сигнатуре, а по контрольной сумме: блок конфигурации у MTK, MSTAR,
    /// HiSilicon и NOR-микросхем начинается с четырёх байт CRC32 всего остального блока.
    /// Такой проверки достаточно, чтобы не спутать таблицу со случайными данными.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            const uint Polynomial = 0xEDB88320u;
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                table[i] = value;
            }

            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// Проверка блока, у которого первые четыре байта — контрольная сумма всего
        /// остального. Именно так устроены блоки конфигурации в дампах телевизоров.
        /// </summary>
        public static bool CheckLeadingChecksum(ReadOnlySpan<byte> block)
        {
            if (block.Length <= 4) return false;

            uint stored = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
            return stored == Compute(block[4..]);
        }
    }
}
