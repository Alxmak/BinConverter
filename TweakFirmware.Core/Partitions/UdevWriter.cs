using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>
    /// Запись списка разделов в формате .udev.
    ///
    /// Формат чужой — это файл устройства программатора UFPI, — и воспроизводится он
    /// затем, чтобы результат разбора можно было открыть в программаторе и сразу писать
    /// разделы в микросхему. Сам по себе для программы он не нужен: внутри список
    /// разделов живёт в <see cref="PartitionTable"/>.
    ///
    /// Файл текстовый, похож на INI, кодировка UTF-8. Строка раздела состоит из семи
    /// полей через запятую: адрес, размер, имя, часть носителя, имя файла, смещение
    /// в файле и файловая система.
    /// </summary>
    public static class UdevWriter
    {
        /// <summary>
        /// Носитель, к которому относятся разделы. Программатор умеет работать и с
        /// служебными областями eMMC, но разбор дампа всегда описывает основную.
        /// </summary>
        private const string UserArea = "USER";

        /// <summary>В каких координатах записан файл.</summary>
        public enum AddressMode
        {
            /// <summary>Адреса и размеры без служебных областей — как их видит прошивка.</summary>
            Logical,

            /// <summary>Адреса и размеры так, как они лежат в дампе NAND.</summary>
            Physical
        }

        public static void Write(
            string path,
            PartitionTable table,
            NandGeometry? geometry = null,
            AddressMode mode = AddressMode.Logical,
            bool includeFileNames = true)
        {
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Write(writer, table, geometry, mode, includeFileNames);
        }

        public static void Write(
            TextWriter writer,
            PartitionTable table,
            NandGeometry? geometry = null,
            AddressMode mode = AddressMode.Logical,
            bool includeFileNames = true)
        {
            writer.NewLine = "\r\n";

            writer.WriteLine("[DESC]");
            writer.WriteLine();
            writer.WriteLine("Name = Partitions");
            writer.WriteLine($"FlashType1 = {(geometry is null ? "eMMC" : "NAND")}");
            writer.WriteLine("FlashBase1 = 0");
            writer.WriteLine();

            writer.WriteLine("[PARTITIONS]");
            writer.WriteLine("PartitionsMode = true");

            if (geometry is not null)
            {
                // Эти два флага говорят программатору, в каких координатах записан файл:
                // с учётом служебных областей или без него.
                writer.WriteLine($"RawAddrMode = {Bool(mode == AddressMode.Physical)}");
                writer.WriteLine($"RawSizeMode = {Bool(mode == AddressMode.Physical)}");
            }

            writer.WriteLine(";Начальный адрес, Размер, Имя, Часть, Имя файла, Смещение в файле, Файловая система");

            foreach (var part in table.Items)
            {
                var entry = mode == AddressMode.Physical && geometry is not null && part.Length >= 0
                    ? part.ToPhysical(geometry)
                    : part;

                string fileName = includeFileNames ? PartitionFileNaming.ForPartition(entry) : "";
                string fs = FileSystemName(entry.FsType);

                writer.WriteLine(string.Join(',',
                    Hex(entry.Offset, 10),
                    Hex(entry.Length, 8),
                    Sanitise(entry.Name),
                    UserArea,
                    fileName,
                    fileName.Length > 0 ? "0" : "",
                    fs));
            }
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string Hex(long value, int digits) =>
            "0x" + value.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        /// <summary>Запятая в имени разорвала бы строку файла на лишние поля.</summary>
        private static string Sanitise(string name) => name.Replace(',', '_');

        /// <summary>
        /// Названия файловых систем, которые понимает программатор. Всё остальное —
        /// SquashFS-подобные образы, tar, VDFS — записывается пустым полем: программатору
        /// они ничего не говорят, а разделы всё равно читаются по адресу и размеру.
        /// </summary>
        private static string FileSystemName(FsType type) => type switch
        {
            FsType.Ext4 => "EXT4",
            FsType.Fat16 => "FAT16",
            FsType.Fat32 => "FAT32",
            FsType.SquashFs => "SQUASHFS",
            _ => ""
        };
    }

    /// <summary>
    /// Имена файлов извлечённых разделов.
    ///
    /// Схема не произвольная: по таким именам программатор умеет обратно наполнять
    /// таблицу разделов кнопкой «Загрузить из папки». Менять её нельзя — сломается
    /// связка с программатором, ради которой всё и делается.
    /// </summary>
    /// <summary>Что удалось прочитать из имени извлечённого файла.</summary>
    public readonly record struct PartitionFileInfo(long Offset, long Length, string Name);

    public static class PartitionFileNaming
    {
        private const string Prefix = "USER_0x";
        private const string HexPrefix = "0x";
        private const string BinExtension = ".bin";

        public static string ForPartition(PartitionEntry part) =>
            ForPartition(part.Offset, part.Length, part.Name, part.FsType);

        public static string ForPartition(long offset, long length, string name, FsType fsType)
        {
            var text = new StringBuilder();

            text.Append(Prefix).Append(offset.ToString("X10", CultureInfo.InvariantCulture));
            text.Append('_').Append(HexPrefix).Append(length.ToString("X8", CultureInfo.InvariantCulture));
            text.Append('_').Append(SanitiseName(name));

            string extension = Extension(fsType);
            if (extension.Length > 0) text.Append('.').Append(extension);

            text.Append(".bin");
            return text.ToString();
        }

        /// <summary>
        /// Обратный разбор имени: адрес, размер и имя раздела.
        ///
        /// Нужен объединению разделов. Всё, что требуется для обратной сборки, программатор
        /// (и мы) кладёт прямо в имя файла — куда этот кусок ложится и какой он длины, —
        /// поэтому папка извлечённых разделов самодостаточна: ни таблицы, ни .udev рядом
        /// не нужно.
        ///
        /// Разбор нестрогий к числу знаков в числах: своё имя мы пишем с фиксированной
        /// шириной (10 и 8), но чужие файлы могли получиться и в другой программе.
        /// А вот нулевую длину не принимаем: раздел без единого байта — это не раздел,
        /// и складывать его в образ нечего.
        /// </summary>
        public static bool TryParse(string? fileName, out PartitionFileInfo info)
        {
            info = default;
            if (string.IsNullOrEmpty(fileName)) return false;

            string name = Path.GetFileName(fileName);

            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!name.EndsWith(BinExtension, StringComparison.OrdinalIgnoreCase)) return false;

            int offsetEnd = name.IndexOf('_', Prefix.Length);
            if (offsetEnd < 0) return false;
            if (!TryHex(name[Prefix.Length..offsetEnd], out long offset)) return false;

            int sizeStart = offsetEnd + 1;
            if (string.Compare(name, sizeStart, HexPrefix, 0, HexPrefix.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;

            sizeStart += HexPrefix.Length;
            int sizeEnd = name.IndexOf('_', sizeStart);
            if (sizeEnd < 0) return false;
            if (!TryHex(name[sizeStart..sizeEnd], out long length) || length <= 0) return false;

            string rest = name[(sizeEnd + 1)..^BinExtension.Length];
            if (rest.Length == 0) return false;

            info = new PartitionFileInfo(offset, length, StripFileSystemExtension(rest));
            return true;
        }

        /// <summary>
        /// Шестнадцатеричное число без знака. Через ulong, потому что long.TryParse
        /// с NumberStyles.HexNumber читает старший бит как знак: «0xFFFFFFFFFFFFFFFF»
        /// стало бы минус единицей вместо отказа.
        /// </summary>
        private static bool TryHex(string text, out long value)
        {
            value = 0;
            if (text.Length is 0 or > 16) return false;

            if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsed))
                return false;

            if (parsed > long.MaxValue) return false;

            value = (long)parsed;
            return true;
        }

        /// <summary>
        /// Убирает расширение файловой системы, которое сами же и дописали: в имени
        /// раздела его не было, а в таблице объединения должно стоять то же имя,
        /// что стояло в таблице разбора.
        /// </summary>
        private static string StripFileSystemExtension(string name)
        {
            foreach (FsType type in Enum.GetValues<FsType>())
            {
                string extension = Extension(type);
                if (extension.Length == 0) continue;

                string suffix = "." + extension;
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name[..^suffix.Length];
            }

            return name;
        }

        /// <summary>Расширение по типу файловой системы — чтобы образ открывался привычной программой.</summary>
        private static string Extension(FsType type) => type switch
        {
            FsType.Ext4 => "ext4",
            FsType.Fat16 => "fat",
            FsType.Fat32 => "fat32",
            FsType.SquashFs => "squashfs",
            FsType.Tar => "tar",
            FsType.RomFs => "romfs",
            FsType.CramFs => "cramfs",
            FsType.Vdfs => "vdfs",
            _ => ""
        };

        /// <summary>
        /// Символы, которые не могут стоять в имени файла Windows, плюс запятая —
        /// она разорвала бы строку .udev на лишние поля.
        ///
        /// Список задан явно, а не взят из <see cref="Path.GetInvalidFileNameChars"/>:
        /// тот зависит от системы, где запущен код, и под Linux содержит всего два
        /// символа. Имя извлечённого раздела должно получаться одинаковым независимо
        /// от того, где программа собрана и запущена.
        /// </summary>
        private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*', ',' };

        /// <summary>
        /// В именах разделов из дампа встречается что угодно, вплоть до символов, которые
        /// файловая система не примет.
        /// </summary>
        private static string SanitiseName(string name)
        {
            var text = new StringBuilder(name.Length);

            foreach (char c in name)
                text.Append(c < 0x20 || Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c);

            string result = text.ToString().Trim();
            return result.Length > 0 ? result : "part";
        }
    }
}
