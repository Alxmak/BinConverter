using System;
using System.Buffers.Binary;
using System.Text;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>Заголовок GPT и его таблица разделов вместе с итогами проверок.</summary>
    public sealed class GptHeader
    {
        public long HeaderOffset { get; init; }
        public uint HeaderLength { get; init; }
        public bool HeaderCrcOk { get; init; }
        public bool TableCrcOk { get; init; }

        public ulong CurrentLba { get; init; }
        public ulong BackupLba { get; init; }
        public ulong FirstUsableLba { get; init; }
        public ulong LastUsableLba { get; init; }
        public ulong DiskGuidHigh { get; init; }
        public ulong DiskGuidLow { get; init; }

        public ulong PartitionTableLba { get; init; }
        public uint PartitionCount { get; init; }
        public uint RecordLength { get; init; }

        public byte[] Table { get; init; } = Array.Empty<byte>();

        /// <summary>Адрес таблицы разделов в байтах.</summary>
        public long TableOffset => (long)PartitionTableLba * GptReader.SectorSize;

        /// <summary>Размер таблицы, выровненный вверх до сектора, — так её добавляют в список разделов.</summary>
        public long TableSizeAligned
        {
            get
            {
                long size = (long)PartitionCount * RecordLength;
                long remainder = size % GptReader.SectorSize;
                return remainder == 0 ? size : size - remainder + GptReader.SectorSize;
            }
        }

        public string DiskGuid => $"{DiskGuidHigh:X16}{DiskGuidLow:X16}";
    }

    /// <summary>
    /// Чтение GPT — таблицы разделов GUID.
    ///
    /// Вызывается из разбора MBR по записи с типом 0xEE и отдельно для резервной копии
    /// таблицы в конце диска. Заголовок и таблица подписаны CRC32, и обе подписи здесь
    /// проверяются: повреждение не мешает прочитать разделы, но о нём надо сказать —
    /// в дампе с битой таблицей смещения могут оказаться мусором.
    /// </summary>
    public static class GptReader
    {
        public const long SectorSize = 0x200;

        /// <summary>«EFI PART» как 64-битное значение в порядке байт диска.</summary>
        private const ulong Signature = 0x5452415020494645UL;

        /// <summary>
        /// Больше этого таблицу разделов не читаем. У настоящей GPT она занимает
        /// 128 записей по 128 байт, то есть 16 КиБ; четыре мегабайта — заведомый запас.
        /// Ограничение защищает от испорченного заголовка, где число разделов оказывается
        /// случайным числом: оригинал в этом случае пытался выделить буфер такого размера.
        /// </summary>
        private const long MaxTableSize = 4L * 1024 * 1024;

        /// <summary>Читает заголовок GPT или возвращает <c>null</c>, если его там нет.</summary>
        public static GptHeader? TryRead(IDumpReader dump, long offset)
        {
            if (offset < 0 || offset + SectorSize > dump.LogicalSize) return null;

            var sector = dump.ReadBlock(offset, (int)SectorSize);
            if (BinaryPrimitives.ReadUInt64LittleEndian(sector) != Signature) return null;

            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x0C));
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x10));

            uint partitionCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x50));
            uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x54));
            uint storedTableCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x58));
            ulong tableLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(0x48));

            bool headerCrcOk = false;
            if (headerLength >= 0x5C && headerLength <= sector.Length)
            {
                // Поле контрольной суммы при подсчёте считается нулевым — так требует
                // спецификация, и так же делал оригинал.
                var forCrc = sector.AsSpan(0, (int)headerLength).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(forCrc.AsSpan(0x10), 0);
                headerCrcOk = Crc32.Compute(forCrc) == storedCrc;
            }

            long tableSize = (long)partitionCount * recordLength;
            byte[] table = Array.Empty<byte>();
            bool tableCrcOk = false;

            if (tableSize > 0 && tableSize <= MaxTableSize)
            {
                long tableOffset = (long)tableLba * SectorSize;
                if (tableOffset >= 0 && tableOffset + tableSize <= dump.LogicalSize)
                {
                    table = dump.ReadBlock(tableOffset, (int)tableSize);
                    tableCrcOk = Crc32.Compute(table) == storedTableCrc;
                }
            }

            return new GptHeader
            {
                HeaderOffset = offset,
                HeaderLength = headerLength,
                HeaderCrcOk = headerCrcOk,
                TableCrcOk = tableCrcOk,
                CurrentLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(0x18)),
                BackupLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(0x20)),
                FirstUsableLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(0x28)),
                LastUsableLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(0x30)),
                DiskGuidHigh = BinaryPrimitives.ReadUInt64BigEndian(sector.AsSpan(0x38)),
                DiskGuidLow = BinaryPrimitives.ReadUInt64BigEndian(sector.AsSpan(0x40)),
                PartitionTableLba = tableLba,
                PartitionCount = partitionCount,
                RecordLength = recordLength,
                Table = table
            };
        }

        /// <summary>
        /// Добавляет в список сам заголовок и таблицу — как отдельные разделы. Это не
        /// разделы диска, но при извлечении их удобно получить файлами: без них дамп
        /// не собрать обратно.
        /// </summary>
        public static void AddHeaderAndTable(GptHeader header, PartitionTable table)
        {
            table.Add("GPT_header", header.HeaderOffset, SectorSize);

            if (header.TableSizeAligned > 0)
                table.Add("GPT_table", header.TableOffset, header.TableSizeAligned);
        }

        /// <summary>
        /// Читает записи таблицы. Перебор останавливается на первой записи с пустым
        /// именем: у GPT незанятые записи идут сплошным хвостом.
        /// </summary>
        public static void ReadParts(GptHeader header, PartitionTable table)
        {
            if (header.Table.Length == 0 || header.RecordLength < 0x40) return;

            for (int i = 0; i < header.PartitionCount; i++)
            {
                long recordStart = (long)i * header.RecordLength;
                if (recordStart + header.RecordLength > header.Table.Length) break;

                var record = header.Table.AsSpan((int)recordStart, (int)header.RecordLength);

                ulong firstLba = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
                ulong lastLba = BinaryPrimitives.ReadUInt64LittleEndian(record[0x28..]);

                string name = ReadName(record);
                if (name.Length == 0) break;

                long offset = (long)firstLba * SectorSize;
                long length = (long)(lastLba - firstLba + 1) * SectorSize;

                table.Add(name, offset, length);
            }
        }

        /// <summary>
        /// Имя раздела. В GPT это 72 байта UTF-16, но оригинал читал 36 байт и оставлял
        /// из них только печатаемые символы — то есть первые восемнадцать знаков имени.
        /// Так и оставлено: по этим именам называются извлечённые файлы и записи в .udev,
        /// и менять их значило бы разойтись с уже сделанными разборами.
        /// </summary>
        private static string ReadName(ReadOnlySpan<byte> record)
        {
            var text = new StringBuilder(18);
            for (int i = 0; i < 36 && 0x38 + i < record.Length; i++)
            {
                byte b = record[0x38 + i];
                if (b > 31 && b < 127) text.Append((char)b);
            }
            return text.ToString();
        }
    }
}
