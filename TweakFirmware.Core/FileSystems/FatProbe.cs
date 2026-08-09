using System;
using System.Text;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>
    /// Опознание файловой системы FAT по загрузочному сектору.
    ///
    /// Помимо очевидной роли — найти том FAT внутри раздела — эта проверка нужна разбору
    /// MBR. Загрузочный сектор FAT тоже кончается сигнатурой 0xAA55, и без дополнительной
    /// проверки любой том FAT в начале дампа был бы принят за таблицу разделов.
    /// </summary>
    public static class FatProbe
    {
        public const int SectorSize = 0x200;

        /// <summary>Сигнатура 0xAA55 в последних двух байтах сектора.</summary>
        public const ushort BootSignature = 0xAA55;

        /// <summary>
        /// Определяет тип FAT по загрузочному сектору или возвращает <see cref="FsType.Unknown"/>.
        ///
        /// Проверок много и они придирчивые: байт на сектор кратен 512 и не больше 8192,
        /// секторов на кластер — степень двойки не больше 64, кластер не больше 32 КиБ.
        /// Так и задумано — функция вызывается на каждом шаге сканирования дампа, и
        /// пропущенное ложное срабатывание превращается в несуществующий раздел.
        /// </summary>
        public static FsType DetectType(IDumpReader dump, long offset)
        {
            var sector = dump.ReadBlock(offset, SectorSize);
            return DetectType(sector);
        }

        /// <summary>Тот же разбор, но по уже прочитанному сектору.</summary>
        public static FsType DetectType(ReadOnlySpan<byte> sector)
        {
            if (sector.Length < SectorSize) return FsType.Unknown;

            if (ReadUInt16(sector, 0x1FE) != BootSignature) return FsType.Unknown;

            uint bytesPerSector = ReadUInt16(sector, 0x0B);
            if (bytesPerSector == 0 || bytesPerSector > 8192 || bytesPerSector % 0x200 != 0)
                return FsType.Unknown;

            uint sectorsPerCluster = sector[0x0D];
            uint clusterSize = sectorsPerCluster * bytesPerSector;
            if (sectorsPerCluster == 0 || clusterSize > 0x8000 || sectorsPerCluster > 64) return FsType.Unknown;

            // Секторов на кластер — либо единица, либо чётное число.
            if (sectorsPerCluster != 1 && sectorsPerCluster % 2 != 0) return FsType.Unknown;

            uint reservedSectors = ReadUInt16(sector, 0x0E);
            if (reservedSectors == 0) return FsType.Unknown;

            uint numberOfFats = sector[0x10];
            if (numberOfFats == 0) return FsType.Unknown;

            uint rootEntryCount = ReadUInt16(sector, 0x11);
            uint fatSize16 = ReadUInt16(sector, 0x16);
            uint fatSize32 = ReadUInt32(sector, 0x24);
            uint totalSectors16 = ReadUInt16(sector, 0x13);
            uint totalSectors32 = ReadUInt32(sector, 0x20);

            uint rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
            uint fatSize = fatSize16 != 0 ? fatSize16 : fatSize32;
            uint totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;

            // Арифметика намеренно беззнаковая и без проверки переполнения — ровно как в
            // оригинале. У испорченного заголовка вычитание уходит в минус и заворачивается
            // в огромное число, отчего том признаётся FAT32, а не отбрасывается. От этого
            // зависит, сработает ли отсев ложных MBR, поэтому поведение оставлено как было.
            uint dataSectors = unchecked(totalSectors - (reservedSectors + numberOfFats * fatSize + rootDirSectors));
            uint countOfClusters = dataSectors / sectorsPerCluster;

            var type = countOfClusters < 65525 ? FsType.Fat16 : FsType.Fat32;

            // Явная метка в заголовке важнее подсчёта кластеров.
            if (ReadLabel(sector, 0x36) == "FAT16   ") return FsType.Fat16;
            if (ReadLabel(sector, 0x52) == "FAT32   ") return FsType.Fat32;

            return type;
        }

        private static string ReadLabel(ReadOnlySpan<byte> sector, int offset) =>
            offset + 8 <= sector.Length ? Encoding.ASCII.GetString(sector.Slice(offset, 8)) : "";

        internal static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
            (ushort)(data[offset] | (data[offset + 1] << 8));

        internal static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    /// <summary>
    /// Зонд тома FAT для сканирования дампа: помимо типа читает метку, размер и
    /// количество свободных кластеров.
    ///
    /// Отделён от <see cref="FatProbe"/> потому, что определение типа нужно ещё и разбору
    /// MBR, где ничего, кроме ответа «это FAT», не требуется, а вот лишнее чтение таблицы
    /// размещения там было бы совсем не к месту.
    /// </summary>
    public sealed class FatVolumeProbe : IFileSystemProbe
    {
        /// <summary>
        /// Предел на размер читаемой таблицы размещения. Свободные кластеры считаются
        /// по ней, и у испорченного заголовка её размер может оказаться любым числом.
        /// </summary>
        private const long MaxFatBytes = 64L * 1024 * 1024;

        public FsType Type => FsType.Fat32;

        public VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd)
        {
            if (offset < 0 || offset + FatProbe.SectorSize > dump.LogicalSize) return null;

            var sector = dump.ReadBlock(offset, FatProbe.SectorSize);
            var type = FatProbe.DetectType(sector);
            if (type is not (FsType.Fat16 or FsType.Fat32)) return null;

            uint bytesPerSector = FatProbe.ReadUInt16(sector, 0x0B);
            uint sectorsPerCluster = sector[0x0D];
            uint reservedSectors = FatProbe.ReadUInt16(sector, 0x0E);
            uint numberOfFats = sector[0x10];
            uint rootEntryCount = FatProbe.ReadUInt16(sector, 0x11);
            uint fatSize16 = FatProbe.ReadUInt16(sector, 0x16);
            uint fatSize32 = FatProbe.ReadUInt32(sector, 0x24);

            uint totalSectors = FatProbe.ReadUInt16(sector, 0x13) != 0
                ? FatProbe.ReadUInt16(sector, 0x13)
                : FatProbe.ReadUInt32(sector, 0x20);

            // Признаки, по которым тип уточняется окончательно: у FAT32 корневой каталог
            // не в фиксированной области, поэтому оба поля обнулены, у FAT16 наоборот.
            if (type == FsType.Fat32 && (rootEntryCount != 0 || fatSize16 != 0)) return null;
            if (type == FsType.Fat16 && (rootEntryCount == 0 || fatSize16 == 0)) return null;

            uint fatSize = fatSize16 != 0 ? fatSize16 : fatSize32;
            long volumeSize = (long)totalSectors * bytesPerSector;
            if (volumeSize <= 0) return null;

            string label = ReadLabel(sector, type == FsType.Fat32 ? 0x47 : 0x2B);
            if (label.Length == 0) label = "NO_NAME";

            long fatOffset = (long)reservedSectors * FatProbe.SectorSize;
            long fatBytes = (long)fatSize * bytesPerSector;

            uint countOfClusters = CountOfClusters(totalSectors, reservedSectors, numberOfFats, fatSize, rootEntryCount, bytesPerSector, sectorsPerCluster);
            long freeClusters = CountFreeClusters(dump, offset + fatOffset, fatBytes, countOfClusters, type);

            var volume = new VolumeInfo(type, label, offset, volumeSize)
            {
                Details = new[]
                {
                    Localization.Strings.Format("Extract_FatVolume",
                        type == FsType.Fat32 ? "FAT32" : "FAT16", label, $"0x{offset:X}", $"0x{volumeSize:X}"),
                    Localization.Strings.Format("Extract_FatGeometry",
                        bytesPerSector, sectorsPerCluster * bytesPerSector),
                    countOfClusters > 0 && freeClusters >= 0
                        ? Localization.Strings.Format("Extract_FatClusters", countOfClusters, freeClusters, freeClusters * 100 / countOfClusters)
                        : ""
                }
            };

            return ProbeHelpers.ClampToPartition(volume, partitionEnd);
        }

        private static uint CountOfClusters(
            uint totalSectors, uint reservedSectors, uint numberOfFats, uint fatSize,
            uint rootEntryCount, uint bytesPerSector, uint sectorsPerCluster)
        {
            uint rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
            uint dataSectors = unchecked(totalSectors - (reservedSectors + numberOfFats * fatSize + rootDirSectors));
            return sectorsPerCluster == 0 ? 0 : dataSectors / sectorsPerCluster;
        }

        /// <summary>
        /// Считает свободные кластеры по таблице размещения: свободный помечен нулём.
        /// Возвращает -1, если таблица слишком велика, чтобы её читать.
        /// </summary>
        private static long CountFreeClusters(IDumpReader dump, long fatOffset, long fatBytes, uint countOfClusters, FsType type)
        {
            if (fatBytes <= 0 || fatBytes > MaxFatBytes) return -1;
            if (fatOffset + fatBytes > dump.LogicalSize) return -1;
            if (countOfClusters == 0) return -1;

            var fat = dump.ReadBlock(fatOffset, (int)fatBytes);
            int entrySize = type == FsType.Fat32 ? 4 : 2;

            long free = 0;
            for (uint cluster = 2; cluster <= countOfClusters + 1; cluster++)
            {
                int at = (int)(cluster * entrySize);
                if (at + entrySize > fat.Length) break;

                uint value = entrySize == 4
                    ? FatProbe.ReadUInt32(fat, at)
                    : FatProbe.ReadUInt16(fat, at);

                if (value == 0) free++;
            }

            return free;
        }

        private static string ReadLabel(byte[] sector, int at)
        {
            if (at + 11 > sector.Length || sector[at] == 0) return "";
            return System.Text.Encoding.ASCII.GetString(sector, at, 11).TrimEnd();
        }
    }
}
