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

        // Пункт 5: раздельные флаги — «Путь по умолчанию» для Конвертирования и для
        // Сборки файла переключаются независимо друг от друга.
        public static bool UseDefaultConvertPath { get; private set; } = true;
        public static bool UseDefaultMergePath { get; private set; } = true;
        public static string CustomConvertFolder { get; private set; } = "";
        public static string CustomMergeFolder { get; private set; } = "";

        public static string DefaultBaseFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tweak Firmware", "New Firmware Files");

        public static string DefaultConvertFolder => Path.Combine(DefaultBaseFolder, "Converted eMMC");
        public static string DefaultMergeFolder => Path.Combine(DefaultBaseFolder, "Merged eMMC");

        static OutputPathSettingsService() => Load();

        /// <summary>Папка, которую сейчас нужно использовать по умолчанию для конвертирования.</summary>
        public static string GetConvertFolder() =>
            UseDefaultConvertPath || string.IsNullOrWhiteSpace(CustomConvertFolder) ? DefaultConvertFolder : CustomConvertFolder;

        /// <summary>Папка, которую сейчас нужно использовать по умолчанию для сборки.</summary>
        public static string GetMergeFolder() =>
            UseDefaultMergePath || string.IsNullOrWhiteSpace(CustomMergeFolder) ? DefaultMergeFolder : CustomMergeFolder;

        public static void SetUseDefaultConvertPath(bool value)
        {
            UseDefaultConvertPath = value;
            Save();
        }

        public static void SetUseDefaultMergePath(bool value)
        {
            UseDefaultMergePath = value;
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

                // "UseDefault" — старый, единый на оба раздела ключ (до разделения флагов);
                // если найден, применяем его значение к обоим новым, иначе не трогаем.
                bool? legacyUseDefault = null;

                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int idx = line.IndexOf('=');
                    if (idx < 0) continue;

                    string key = line[..idx];
                    string value = line[(idx + 1)..];

                    switch (key)
                    {
                        case "UseDefault": legacyUseDefault = value == "True"; break;
                        case "UseDefaultConvert": UseDefaultConvertPath = value == "True"; break;
                        case "UseDefaultMerge": UseDefaultMergePath = value == "True"; break;
                        case "ConvertFolder": CustomConvertFolder = value; break;
                        case "MergeFolder": CustomMergeFolder = value; break;
                    }
                }

                if (legacyUseDefault.HasValue)
                {
                    UseDefaultConvertPath = legacyUseDefault.Value;
                    UseDefaultMergePath = legacyUseDefault.Value;
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
                    $"UseDefaultConvert={UseDefaultConvertPath}",
                    $"UseDefaultMerge={UseDefaultMergePath}",
                    $"ConvertFolder={CustomConvertFolder}",
                    $"MergeFolder={CustomMergeFolder}"
                });
            }
            catch { /* сохранение путей не критично для работы программы */ }
        }
    }
}
