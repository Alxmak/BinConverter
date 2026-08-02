using System;
using System.IO;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Хранит выбор пользователя насчёт папок вывода для разделов «Конвертирование»
    /// и «Сборка файла»: либо папки по умолчанию (внутри Документы\Tweak Firmware\
    /// New Firmware Files), либо собственные папки, указанные в настройках.
    /// Сохранение между запусками — простой текстовый файл, аналогично ThemeService.
    /// </summary>
    public static class OutputPathSettingsService
    {
        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "paths.txt");

        public static bool UseDefaultPaths { get; private set; } = true;
        public static string CustomConvertFolder { get; private set; } = "";
        public static string CustomMergeFolder { get; private set; } = "";

        public static string DefaultBaseFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tweak Firmware", "New Firmware Files");

        public static string DefaultConvertFolder => Path.Combine(DefaultBaseFolder, "Converted eMMC");
        public static string DefaultMergeFolder => Path.Combine(DefaultBaseFolder, "Merged eMMC");

        static OutputPathSettingsService() => Load();

        /// <summary>Папка, которую сейчас нужно использовать по умолчанию для конвертирования.</summary>
        public static string GetConvertFolder() =>
            UseDefaultPaths || string.IsNullOrWhiteSpace(CustomConvertFolder) ? DefaultConvertFolder : CustomConvertFolder;

        /// <summary>Папка, которую сейчас нужно использовать по умолчанию для сборки.</summary>
        public static string GetMergeFolder() =>
            UseDefaultPaths || string.IsNullOrWhiteSpace(CustomMergeFolder) ? DefaultMergeFolder : CustomMergeFolder;

        public static void SetUseDefaultPaths(bool value)
        {
            UseDefaultPaths = value;
            Save();
        }

        public static void SetCustomConvertFolder(string folder)
        {
            CustomConvertFolder = folder;
            Save();
        }

        public static void SetCustomMergeFolder(string folder)
        {
            CustomMergeFolder = folder;
            Save();
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;

                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int idx = line.IndexOf('=');
                    if (idx < 0) continue;

                    string key = line[..idx];
                    string value = line[(idx + 1)..];

                    switch (key)
                    {
                        case "UseDefault": UseDefaultPaths = value == "True"; break;
                        case "ConvertFolder": CustomConvertFolder = value; break;
                        case "MergeFolder": CustomMergeFolder = value; break;
                    }
                }
            }
            catch { /* используем значения по умолчанию, если не удалось прочитать */ }
        }

        private static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllLines(SettingsPath, new[]
                {
                    $"UseDefault={UseDefaultPaths}",
                    $"ConvertFolder={CustomConvertFolder}",
                    $"MergeFolder={CustomMergeFolder}"
                });
            }
            catch { /* сохранение путей не критично для работы программы */ }
        }
    }
}
