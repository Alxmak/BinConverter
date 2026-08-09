using System;
using System.IO;
using System.Text;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Классическая разметка: MBR, цепочка EBR и GPT. Дампы собираются синтетически —
    /// ровно с теми полями, которые читает детектор.
    /// </summary>
    public class MbrGptDetectorTests
    {
        private const long Sector = 0x200;

        private static DumpBuilder WithMbr(DumpBuilder builder, long mbrOffset,
            params (byte Type, uint StartSector, uint SectorCount)[] records)
        {
            for (int i = 0; i < records.Length && i < 4; i++)
            {
                long record = mbrOffset + 0x1BE + i * 16;
                builder.Write(record + 0x04, records[i].Type);
                builder.WriteUInt32(record + 0x08, records[i].StartSector);
                builder.WriteUInt32(record + 0x0C, records[i].SectorCount);
            }

            builder.WriteUInt16(mbrOffset + 0x1FE, 0xAA55);
            return builder;
        }

        private static (PartitionTable Table, SilentAnalysisHost Host) Run(byte[] image)
        {
            var dump = new PlainDumpReader(new MemoryStream(image));
            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();
            var detector = new MbrDetector();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            Assert.NotNull(detection);
            detector.ReadParts(detection!, table, dump, context, host, CancellationToken.None);

            return (table, host);
        }

        [Fact]
        public void Detects_PlainMbrWithTwoPartitions()
        {
            var image = WithMbr(new DumpBuilder(1 << 20), 0,
                (0x83, 2, 100),
                (0x83, 200, 100)).Build();

            var (table, _) = Run(image);

            // Сама запись MBR тоже становится разделом: без неё дамп не собрать обратно.
            Assert.Equal("MBR", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(Sector, table[0].Length);

            // Нумерация начинается с part_00 — как в оригинале.
            Assert.Equal("part_00", table[1].Name);
            Assert.Equal(2 * Sector, table[1].Offset);
            Assert.Equal(100 * Sector, table[1].Length);
            Assert.Equal(0x83, table[1].MbrType);

            Assert.Equal("part_01", table[2].Name);
            Assert.Equal(200 * Sector, table[2].Offset);
        }

        [Fact]
        public void Ignores_RecordsWithZeroLength()
        {
            var image = WithMbr(new DumpBuilder(1 << 20), 0,
                (0x83, 2, 100),
                (0x83, 200, 0)).Build();

            var (table, _) = Run(image);

            Assert.Equal(2, table.Count);   // MBR + один раздел
        }

        [Fact]
        public void DoesNotTreatAFatBootSectorAsAPartitionTable()
        {
            // У загрузочного сектора FAT та же сигнатура 0xAA55 в конце.
            var builder = new DumpBuilder(1 << 20);
            builder.WriteUInt16(0x0B, 512);      // байт на сектор
            builder.Write(0x0D, 8);              // секторов на кластер
            builder.WriteUInt16(0x0E, 1);        // зарезервированных секторов
            builder.Write(0x10, 2);              // копий FAT
            builder.WriteUInt16(0x11, 512);      // записей корневого каталога
            builder.WriteUInt16(0x13, 2048);     // всего секторов
            builder.WriteUInt16(0x16, 4);        // размер FAT
            builder.WriteAscii(0x36, "FAT16   ");
            builder.WriteUInt16(0x1FE, 0xAA55);

            var dump = new PlainDumpReader(new MemoryStream(builder.Build()));
            var context = new DumpContext(dump);

            Assert.Null(new MbrDetector().TryDetect(dump, context, new SilentAnalysisHost(), CancellationToken.None));
        }

        [Fact]
        public void DoesNotDetectAnythingWithoutTheBootSignature()
        {
            var image = new DumpBuilder(1 << 20).Pattern(0, 1024).Build();

            var dump = new PlainDumpReader(new MemoryStream(image));
            var context = new DumpContext(dump);

            Assert.Null(new MbrDetector().TryDetect(dump, context, new SilentAnalysisHost(), CancellationToken.None));
        }

        [Fact]
        public void WalksTheExtendedPartitionChain()
        {
            const uint ExtendedStart = 1000;
            var builder = new DumpBuilder(4 << 20);

            WithMbr(builder, 0, (0x83, 2, 100), (0x05, ExtendedStart, 2000));

            // Первая запись цепочки: раздел сразу за ней и указатель на следующую.
            long ebr1 = ExtendedStart * Sector;
            WithMbr(builder, ebr1, (0x83, 1, 400), (0x05, 500, 600));

            // Вторая запись: раздел и конец цепочки.
            long ebr2 = (ExtendedStart + 500) * Sector;
            WithMbr(builder, ebr2, (0x83, 1, 300), (0x00, 0, 0));

            var (table, _) = Run(builder.Build());

            Assert.Contains(table, p => p.Name == "EBR_01" && p.Offset == ebr1);
            Assert.Contains(table, p => p.Name == "EBR_02" && p.Offset == ebr2);

            // В цепочке счётчик увеличивается до выдачи имени, поэтому нумерация здесь
            // продолжается с part_01 после единственного основного раздела part_00.
            Assert.Contains(table, p => p.Name == "part_00");
            Assert.Contains(table, p => p.Offset == ebr1 + Sector && p.Length == 400 * Sector);
            Assert.Contains(table, p => p.Offset == ebr2 + Sector && p.Length == 300 * Sector);
        }

        [Fact]
        public void StopsWhenTheChainPointsOutsideTheDump()
        {
            var builder = new DumpBuilder(1 << 20);
            WithMbr(builder, 0, (0x05, 4000, 100));   // за пределами дампа в 1 МиБ

            var (table, host) = Run(builder.Build());

            Assert.Contains(table, p => p.Name == "EBR_01");
            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
        }

        [Fact]
        public void ReadsGptBehindTheProtectiveRecord()
        {
            const int PartitionCount = 4;
            const int RecordLength = 128;
            const long HeaderLba = 1;
            const long TableLba = 2;
            const long BackupLba = 500;

            var builder = new DumpBuilder(1 << 20);
            WithMbr(builder, 0, (0xEE, (uint)HeaderLba, 2000));

            var names = new[] { "boot", "system", "userdata", "" };
            var records = new byte[PartitionCount * RecordLength];
            for (int i = 0; i < PartitionCount; i++)
            {
                if (names[i].Length == 0) continue;

                ulong first = (ulong)(100 + i * 100);
                ulong last = first + 99;

                WriteUInt64(records, i * RecordLength + 0x20, first);
                WriteUInt64(records, i * RecordLength + 0x28, last);

                // Имя в GPT записано в UTF-16, но детектор читает только печатаемые байты.
                var utf16 = Encoding.Unicode.GetBytes(names[i]);
                utf16.CopyTo(records, i * RecordLength + 0x38);
            }

            builder.Write(TableLba * Sector, records);
            WriteGptHeader(builder, HeaderLba, TableLba, BackupLba, PartitionCount, RecordLength, records);

            // Резервная копия заголовка в конце диска — её детектор проверяет отдельно.
            WriteGptHeader(builder, BackupLba, TableLba, HeaderLba, PartitionCount, RecordLength, records);

            var (table, host) = Run(builder.Build());

            Assert.Contains(table, p => p.Name == "GPT_header" && p.Offset == HeaderLba * Sector);
            Assert.Contains(table, p => p.Name == "GPT_table" && p.Offset == TableLba * Sector);
            Assert.Contains(table, p => p.Name == "GPT_header" && p.Offset == BackupLba * Sector);

            Assert.Contains(table, p => p.Name == "boot" && p.Offset == 100 * Sector && p.Length == 100 * Sector);
            Assert.Contains(table, p => p.Name == "system" && p.Offset == 200 * Sector);
            Assert.Contains(table, p => p.Name == "userdata" && p.Offset == 300 * Sector);

            // Запись с пустым именем обрывает перебор: у GPT незанятые записи идут хвостом.
            Assert.Equal(3, CountDataPartitions(table));

            // Контрольные суммы сошлись — предупреждений о повреждении быть не должно.
            Assert.DoesNotContain(host.Messages, m => m.Message.Contains("GPT") && m.Level == AnalysisLogLevel.Warning);
        }

        [Fact]
        public void GptNameIsReadToItsFullWidth()
        {
            // Имя в GPT — 72 байта UTF-16, то есть 36 знаков. Оригинал читал 36 байт,
            // и от такого имени оставалась ровно первая половина.
            const string Name = "persistent_storage_partition";
            const int RecordLength = 128;

            var builder = new DumpBuilder(1 << 20);
            WithMbr(builder, 0, (0xEE, 1, 2000));

            var records = new byte[RecordLength];
            WriteUInt64(records, 0x20, 100);
            WriteUInt64(records, 0x28, 199);
            Encoding.Unicode.GetBytes(Name).CopyTo(records, 0x38);

            builder.Write(2 * Sector, records);
            WriteGptHeader(builder, 1, 2, 500, 1, RecordLength, records);

            var (table, _) = Run(builder.Build());

            Assert.Contains(table, p => p.Name == Name);
        }

        [Fact]
        public void ReportsACorruptedGptTableButStillReadsIt()
        {
            const int PartitionCount = 2;
            const int RecordLength = 128;

            var builder = new DumpBuilder(1 << 20);
            WithMbr(builder, 0, (0xEE, 1, 2000));

            var records = new byte[PartitionCount * RecordLength];
            WriteUInt64(records, 0x20, 100);
            WriteUInt64(records, 0x28, 199);
            Encoding.Unicode.GetBytes("boot").CopyTo(records, 0x38);

            builder.Write(2 * Sector, records);
            WriteGptHeader(builder, 1, 2, 500, PartitionCount, RecordLength, records);

            // Портим таблицу уже после того, как её сумма попала в заголовок.
            builder.Write(2 * Sector + 0x38, 0x21);

            var (table, host) = Run(builder.Build());

            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
            Assert.Contains(table, p => p.Name == "GPT_header");
        }

        [Fact]
        public void AddsNothingWhenTheProtectiveRecordPointsNowhere()
        {
            // Оригинал в этом случае разбирал содержимое буфера, оставшееся от прошлых
            // чтений, и добавлял в таблицу мусор.
            var image = WithMbr(new DumpBuilder(1 << 20), 0, (0xEE, 1, 2000)).Build();

            var (table, host) = Run(image);

            Assert.DoesNotContain(table, p => p.Name.StartsWith("GPT"));
            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
        }

        private static void WriteGptHeader(
            DumpBuilder builder, long headerLba, long tableLba, long backupLba,
            int partitionCount, int recordLength, byte[] records)
        {
            var header = new byte[Sector];

            Encoding.ASCII.GetBytes("EFI PART").CopyTo(header, 0);
            WriteUInt32(header, 0x0C, 0x5C);                    // длина заголовка
            WriteUInt64(header, 0x18, (ulong)headerLba);
            WriteUInt64(header, 0x20, (ulong)backupLba);
            WriteUInt64(header, 0x28, 34);                      // первый доступный сектор
            WriteUInt64(header, 0x30, 1000);                    // последний доступный сектор
            WriteUInt64(header, 0x48, (ulong)tableLba);
            WriteUInt32(header, 0x50, (uint)partitionCount);
            WriteUInt32(header, 0x54, (uint)recordLength);
            WriteUInt32(header, 0x58, Crc32.Compute(records));

            // Сумма заголовка считается по нулевому полю суммы.
            WriteUInt32(header, 0x10, 0);
            WriteUInt32(header, 0x10, Crc32.Compute(header.AsSpan(0, 0x5C)));

            builder.Write(headerLba * Sector, header);
        }

        /// <summary>Разделы диска — всё, кроме служебных записей самой разметки.</summary>
        private static int CountDataPartitions(PartitionTable table)
        {
            int count = 0;
            foreach (var part in table)
            {
                if (part.Name is "MBR" or "GPT_header" or "GPT_table") continue;
                if (part.Name.StartsWith("EBR_")) continue;
                count++;
            }
            return count;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            for (int i = 0; i < 4; i++) buffer[offset + i] = (byte)(value >> (i * 8));
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++) buffer[offset + i] = (byte)(value >> (i * 8));
        }
    }
}
