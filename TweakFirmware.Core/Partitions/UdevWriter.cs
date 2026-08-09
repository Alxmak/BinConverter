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
    public static class PartitionFileNaming
    {
        public static string ForPartition(PartitionEntry part) =>
            ForPartition(part.Offset, part.Length, part.Name, part.FsType);

        public static string ForPartition(long offset, long length, string name, FsType fsType)
        {
            var text = new StringBuilder();

            text.Append("USER_0x").Append(offset.ToString("X10", CultureInfo.InvariantCulture));
            text.Append("_0x").Append(length.ToString("X8", CultureInfo.InvariantCulture));
            text.Append('_').Append(SanitiseName(name));

            string extension = Extension(fsType);
            if (extension.Length > 0) text.Append('.').Append(extension);

            text.Append(".bin");
            return text.ToString();
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
