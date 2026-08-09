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
}
