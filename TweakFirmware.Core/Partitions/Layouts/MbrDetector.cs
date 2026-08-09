using System;
using System.Buffers.Binary;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>Одна из четырёх записей таблицы разделов MBR или EBR.</summary>
    public readonly struct MbrRecord
    {
        public byte Status { get; init; }
        public byte Type { get; init; }
        public uint OffsetInSectors { get; init; }
        public uint LengthInSectors { get; init; }

        public bool IsEmpty => Status == 0 && Type == 0 && OffsetInSectors == 0 && LengthInSectors == 0;

        /// <summary>Расширенный раздел: внутри него лежит цепочка записей EBR.</summary>
        public bool IsExtended => Type is 0x05 or 0x0F or 0x85;

        /// <summary>Защитная запись, за которой стоит настоящая таблица GPT.</summary>
        public bool IsGptProtective => Type == 0xEE;

        public static MbrRecord Parse(ReadOnlySpan<byte> table, int index)
        {
            var record = table.Slice(index * 16, 16);
            return new MbrRecord
            {
                Status = record[0],
                Type = record[4],
                OffsetInSectors = BinaryPrimitives.ReadUInt32LittleEndian(record[0x08..]),
                LengthInSectors = BinaryPrimitives.ReadUInt32LittleEndian(record[0x0C..])
            };
        }
    }

    /// <summary>
    /// Классическая разметка диска: MBR, цепочка EBR внутри расширенного раздела и GPT,
    /// на которую указывает запись с типом 0xEE.
    ///
    /// Самая частая разметка в дампах eMMC и первая, к которой стоит обращаться. Дальше
    /// всё зависит от типов записей: обычный раздел добавляется как есть, расширенный
    /// разворачивается в цепочку, а защитная запись GPT передаёт работу
    /// <see cref="GptReader"/>.
    /// </summary>
    public sealed class MbrDetector : ILayoutDetector
    {
        public const long SectorSize = 0x200;
        private const ushort BootSignature = 0xAA55;

        /// <summary>
        /// Предел длины цепочки EBR. У оригинала его не было: записи читались рекурсивно,
        /// и дамп, в котором цепочка замкнута в кольцо (а испорченный дамп — обычное дело
        /// в ремонте), подвешивал разбор насмерть. Настоящие цепочки короче на порядки.
        /// </summary>
        private const int MaxEbrChainLength = 128;

        public string Name => "MBR";

        public int Priority => 60;

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, CancellationToken ct)
        {
            const long Offset = 0;

            if (!dump.Contains(Offset, SectorSize)) return null;

            var sector = dump.ReadBlock(Offset, (int)SectorSize);
            if (BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x1FE)) != BootSignature) return null;

            // Загрузочный сектор FAT кончается той же сигнатурой. Без этой проверки
            // любой том FAT в начале дампа был бы принят за таблицу разделов.
            if (FatProbe.DetectType(sector) != FsType.Unknown) return null;

            return new LayoutDetection(Name, Offset, sector);
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            context.MbrPresent = true;

            long offsetMbr = detection.TableOffset;
            table.Add("MBR", offsetMbr, SectorSize);

            if (!TryReadRecords(detection.Table, out var records)) return;

            LogRecords(records, host);

            for (int i = 0; i < records.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var record = records[i];
                if (record.LengthInSectors == 0) continue;

                if (record.IsGptProtective)
                {
                    ReadGpt(dump, table, host, (long)record.OffsetInSectors * SectorSize);
                    continue;
                }

                if (record.IsExtended)
                {
                    ReadExtendedChain(dump, table, host, offsetMbr + (long)record.OffsetInSectors * SectorSize, ct);
                    continue;
                }

                // Имя берётся до увеличения счётчика — поэтому первый раздел получает
                // part_00. В цепочке EBR счётчик увеличивается раньше, и там нумерация
                // начинается с part_01. Несогласованность перенесена из оригинала: по этим
                // именам называются извлечённые файлы и записи в уже сделанных .udev.
                table.Add(
                    $"part_{table.AutoPartCounter:D2}",
                    offsetMbr + (long)record.OffsetInSectors * SectorSize,
                    (long)record.LengthInSectors * SectorSize,
                    FsType.Unknown,
                    "",
                    record.Status,
                    record.Type);

                table.AutoPartCounter++;
            }
        }

        private static void LogRecords(MbrRecord[] records, IAnalysisHost host)
        {
            host.Log(Strings.Get("Extract_MbrRecordsHeader"));
            for (int i = 0; i < records.Length; i++)
            {
                var r = records[i];
                host.Log(Strings.Format(
                    "Extract_MbrRecordLine",
                    i,
                    $"0x{r.Type:X2}",
                    $"0x{(long)r.OffsetInSectors * SectorSize:X}",
                    $"0x{(long)r.LengthInSectors * SectorSize:X}"));
            }
        }

        private void ReadGpt(IDumpReader dump, PartitionTable table, IAnalysisHost host, long offset)
        {
            var header = GptReader.TryRead(dump, offset);
            if (header is null)
            {
                // Оригинал в этом случае всё равно шёл разбирать записи и добавлял мусор
                // из непрочитанного буфера. Здесь при отсутствии заголовка не добавляется
                // ничего — это единственная разница, и она в пользу пустого результата.
                host.Log(Strings.Format("Extract_GptHeaderMissing", $"0x{offset:X}"), AnalysisLogLevel.Warning);
                return;
            }

            LogGpt(header, host);
            GptReader.AddHeaderAndTable(header, table);
            GptReader.ReadParts(header, table);

            // Резервная копия таблицы лежит в конце диска и проверяется отдельно:
            // по ней видно, целостна ли разметка вообще.
            long backupOffset = (long)header.BackupLba * GptReader.SectorSize;
            if (backupOffset == offset || backupOffset <= 0) return;

            var backup = GptReader.TryRead(dump, backupOffset);
            if (backup is null)
            {
                host.Log(Strings.Format("Extract_GptBackupMissing", $"0x{backupOffset:X}"), AnalysisLogLevel.Warning);
                return;
            }

            LogGpt(backup, host);
            GptReader.AddHeaderAndTable(backup, table);
        }

        private static void LogGpt(GptHeader header, IAnalysisHost host)
        {
            host.Log(Strings.Format("Extract_GptHeaderAt", $"0x{header.HeaderOffset:X}"), AnalysisLogLevel.Found);
            host.Log(Strings.Format("Extract_GptTableAt", $"0x{header.TableOffset:X}", header.PartitionCount));
            host.Log(Strings.Format("Extract_GptDiskGuid", header.DiskGuid));

            if (!header.HeaderCrcOk) host.Log(Strings.Get("Extract_GptHeaderCorrupt"), AnalysisLogLevel.Warning);
            if (!header.TableCrcOk) host.Log(Strings.Get("Extract_GptTableCorrupt"), AnalysisLogLevel.Warning);
        }

        /// <summary>
        /// Разворачивает цепочку записей EBR. Каждая такая запись — сектор с двумя
        /// значащими полями: первое описывает раздел относительно самой записи, второе
        /// указывает на следующую запись относительно начала расширенного раздела.
        /// </summary>
        private void ReadExtendedChain(
            IDumpReader dump,
            PartitionTable table,
            IAnalysisHost host,
            long extendedStart,
            CancellationToken ct)
        {
            long next = extendedStart;

            for (int step = 0; step < MaxEbrChainLength; step++)
            {
                ct.ThrowIfCancellationRequested();

                table.EbrCounter++;
                table.Add($"EBR_{table.EbrCounter:D2}", next, SectorSize);

                if (!dump.Contains(next, SectorSize))
                {
                    host.Log(Strings.Format("Extract_EbrOutsideDump", $"0x{next:X}"), AnalysisLogLevel.Warning);
                    return;
                }

                var sector = dump.ReadBlock(next, (int)SectorSize);
                if (BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x1FE)) != BootSignature)
                {
                    host.Log(Strings.Format("Extract_EbrNotFound", $"0x{next:X}"), AnalysisLogLevel.Warning);
                    return;
                }

                if (!TryReadRecords(sector, out var records) || !RecordsLookValid(records))
                {
                    host.Log(Strings.Format("Extract_EbrBadRecords", $"0x{next:X}"), AnalysisLogLevel.Warning);
                    return;
                }

                host.Log(Strings.Format("Extract_EbrAt", $"0x{next:X}"), AnalysisLogLevel.Found);

                // Здесь счётчик увеличивается до выдачи имени — в отличие от разделов MBR.
                table.AutoPartCounter++;
                table.Add(
                    $"part_{table.AutoPartCounter:D2}",
                    next + (long)records[0].OffsetInSectors * SectorSize,
                    (long)records[0].LengthInSectors * SectorSize,
                    FsType.Unknown,
                    "",
                    records[0].Status,
                    records[0].Type);

                if (!records[1].IsExtended) return;

                // Следующая запись адресуется от начала расширенного раздела, а не от текущей.
                next = extendedStart + (long)records[1].OffsetInSectors * SectorSize;
            }

            host.Log(Strings.Get("Extract_EbrChainTooLong"), AnalysisLogLevel.Warning);
        }

        /// <summary>
        /// Читает четыре записи из сектора. Возвращает <c>false</c>, если область таблицы
        /// пуста: значит, это не таблица разделов.
        /// </summary>
        private static bool TryReadRecords(ReadOnlySpan<byte> sector, out MbrRecord[] records)
        {
            records = Array.Empty<MbrRecord>();
            if (sector.Length < 0x200) return false;

            var area = sector.Slice(0x1BE, 0x40);
            if (IsBlank(area)) return false;

            records = new MbrRecord[4];
            for (int i = 0; i < 4; i++) records[i] = MbrRecord.Parse(area, i);
            return true;
        }

        /// <summary>
        /// Проверка «область таблицы пуста». Условие взято из оригинала дословно, вместе
        /// со сравнением восьмибайтового слова с 0xFF вместо 0xFFFFFFFFFFFFFFFF. Разницы
        /// здесь нет: сюда попадают только секторы, в которых уже нашлась сигнатура
        /// 0xAA55, а значит, область из одних байт 0xFF в них невозможна.
        /// </summary>
        private static bool IsBlank(ReadOnlySpan<byte> area)
        {
            for (int i = 0; i < area.Length / 8; i++)
            {
                ulong word = BinaryPrimitives.ReadUInt64LittleEndian(area[(i * 8)..]);
                if (word == 0 || word == 0xFF) continue;
                return false;
            }
            return true;
        }

        /// <summary>Статус записи бывает только загрузочный, обычный или пара редких значений.</summary>
        private static bool RecordsLookValid(MbrRecord[] records)
        {
            foreach (var record in records)
            {
                if (record.Status is not (0x00 or 0x80 or 0x03 or 0x01)) return false;
            }
            return true;
        }
    }
}
