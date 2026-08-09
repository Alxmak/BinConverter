using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.Analysis
{
    /// <summary>Что удалось узнать о прошивке Android из файла build.prop.</summary>
    public sealed class AndroidInfo
    {
        public string Brand { get; init; } = "";
        public string Name { get; init; } = "";
        public string Device { get; init; } = "";
        public string Release { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Board { get; init; } = "";
        public string Model { get; init; } = "";
        public string SoftwareVersion { get; init; } = "";
        public string HardwareBoard { get; init; } = "";
        public string DisplaySize { get; init; } = "";

        /// <summary>Содержимое найденного файла — его сохраняют рядом с дампом.</summary>
        public byte[] RawBuildProp { get; init; } = Array.Empty<byte>();

        public bool IsEmpty =>
            Brand.Length == 0 && Name.Length == 0 && Device.Length == 0 && Model.Length == 0;
    }

    /// <summary>
    /// Поиск сведений об Android-прошивке в дампе.
    ///
    /// Все нужные строки лежат в файле build.prop внутри тома ext4. Проще всего он
    /// находится в разделе с именем system; если такого нет — перебираются все тома ext4
    /// подряд. Внутри тома файл ищется в корне, а если его там нет — в каталоге system:
    /// у части прошивок корень тома и есть /system, у части — на уровень выше.
    /// </summary>
    public static class AndroidInfoReader
    {
        private const string BuildPropName = "build.prop";
        private const string SystemDirectory = "system";

        public static AndroidInfo? TryRead(
            IDumpReader dump,
            PartitionTable table,
            IAnalysisHost host,
            CancellationToken ct = default)
        {
            foreach (var part in OrderedCandidates(table))
            {
                ct.ThrowIfCancellationRequested();

                var info = TryReadFromPartition(dump, part, ct);
                if (info is null) continue;

                Report(info, host);
                return info;
            }

            host.Log(Strings.Get("Extract_AndroidNotFound"), AnalysisLogLevel.Warning);
            return null;
        }

        /// <summary>
        /// Разделы в порядке осмотра: сначала тот, что называется system, потом все
        /// остальные тома ext4. Так первый же осмотренный раздел обычно и оказывается
        /// нужным, а перебор остальных — запасной путь.
        /// </summary>
        private static IEnumerable<PartitionEntry> OrderedCandidates(PartitionTable table)
        {
            foreach (var part in table.Items)
                if (part.FsType == FsType.Ext4 && part.Name == SystemDirectory) yield return part;

            foreach (var part in table.Items)
                if (part.FsType == FsType.Ext4 && part.Name != SystemDirectory) yield return part;
        }

        private static AndroidInfo? TryReadFromPartition(IDumpReader dump, PartitionEntry part, CancellationToken ct)
        {
            var superblock = Ext4Probe.TryReadSuperblock(dump, part.Offset);
            if (superblock is null) return null;

            var reader = Ext4DirectoryReader.Open(dump, superblock);
            if (reader is null) return null;

            var root = reader.ReadRoot();

            var file = reader.Find(root, BuildPropName);
            if (file is null)
            {
                // Файла в корне нет — возможно, том смонтирован на уровень выше /system.
                var systemDir = reader.Find(root, SystemDirectory);
                if (systemDir is null) return null;

                ct.ThrowIfCancellationRequested();
                file = reader.Find(reader.ReadDirectory(systemDir.Inode), BuildPropName);
                if (file is null) return null;
            }

            var content = reader.ReadFile(file.Inode);
            if (content is null || content.Length == 0) return null;

            var info = Parse(content);
            return info.IsEmpty ? null : info;
        }

        /// <summary>
        /// Разбирает build.prop. Сперва пробуется строка отпечатка сборки: в ней через
        /// косые черты и двоеточие записано сразу всё главное. Если её нет, поля читаются
        /// по отдельности — так устроены прошивки, собранные не по образцу Google.
        /// </summary>
        public static AndroidInfo Parse(byte[] content)
        {
            string text = Encoding.UTF8.GetString(content);

            string brand = "", name = "", device = "", release = "";

            string fingerprint = ReadValue(text, ".fingerprint=");
            if (fingerprint.Length > 0)
            {
                // Вид: brand/name/device:release/id/version:type/tags
                var parts = fingerprint.Split('/');
                if (parts.Length >= 3)
                {
                    brand = parts[0];
                    name = parts[1];

                    var deviceAndRelease = parts[2].Split(':');
                    device = deviceAndRelease[0];
                    if (deviceAndRelease.Length > 1) release = deviceAndRelease[1];
                }
            }

            if (brand.Length == 0) brand = ReadValue(text, ".brand=");
            if (name.Length == 0) name = ReadValue(text, ".name=");
            if (device.Length == 0) device = ReadValue(text, ".device=");
            if (release.Length == 0) release = ReadValue(text, ".release=");

            return new AndroidInfo
            {
                Brand = brand,
                Name = name,
                Device = device,
                Release = release,
                Manufacturer = ReadValue(text, ".manufacturer="),
                Platform = ReadValue(text, ".platform="),
                Board = ReadValue(text, ".board="),
                Model = ReadValue(text, ".model="),
                SoftwareVersion = ReadValue(text, ".swversion="),
                HardwareBoard = ReadValue(text, ".hwboard="),
                DisplaySize = ReadValue(text, ".display-size="),
                RawBuildProp = content
            };
        }

        /// <summary>
        /// Значение свойства по хвосту его имени. Ищется именно хвост: у одного и того же
        /// поля префикс бывает разным — ro.build.version, ro.product.vendor и так далее.
        /// </summary>
        private static string ReadValue(string text, string keySuffix)
        {
            int at = text.IndexOf(keySuffix, StringComparison.Ordinal);
            if (at < 0) return "";

            int start = at + keySuffix.Length;
            int end = start;
            while (end < text.Length && text[end] is not ('\n' or '\r' or '\0')) end++;

            return text[start..end].Trim();
        }

        private static void Report(AndroidInfo info, IAnalysisHost host)
        {
            host.Log(Strings.Get("Extract_AndroidHeader"), AnalysisLogLevel.Found);

            Line("Extract_AndroidBrand", info.Brand);
            Line("Extract_AndroidName", info.Name);
            Line("Extract_AndroidDevice", info.Device);
            Line("Extract_AndroidRelease", info.Release);
            Line("Extract_AndroidManufacturer", info.Manufacturer);
            Line("Extract_AndroidPlatform", info.Platform);
            Line("Extract_AndroidBoard", info.Board);
            Line("Extract_AndroidModel", info.Model);
            Line("Extract_AndroidSwVersion", info.SoftwareVersion);
            Line("Extract_AndroidHwBoard", info.HardwareBoard);
            Line("Extract_AndroidDisplaySize", info.DisplaySize);

            void Line(string key, string value)
            {
                if (value.Length > 0) host.Log(Strings.Format(key, value));
            }
        }
    }
}
