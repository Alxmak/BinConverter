using System;
using System.Collections.Generic;
using System.IO;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Галочки рабочих вкладок между запусками.
    ///
    /// Все восемь стоят по умолчанию, и все восемь до сих пор возвращались в это
    /// положение при каждом запуске. Для того, кто снимает одну и ту же — скажем,
    /// «Проверить свободное место» при работе с сетевой папкой, где свободное место
    /// не спросишь, — это значило снимать её заново перед каждой операцией, и однажды
    /// забыть об этом ровно тогда, когда она мешает.
    ///
    /// Значения по умолчанию остаются прежними и живут не здесь, а у самих вкладок:
    /// служба знает только то, что человек менял. Отсюда и <c>fallback</c> у чтения —
    /// нетронутая галочка в файл не попадает вовсе, и её положение задаёт вкладка.
    ///
    /// Хранение — тот же простой текстовый файл рядом с темой и путями: настройки
    /// программы читаются глазами и правятся блокнотом, а формат посерьёзнее здесь
    /// нечего описывать.
    /// </summary>
    public static class TabOptionsService
    {
        // Ключи держим здесь, а не строками по вкладкам: имя ключа попадает в файл
        // на диске, и опечатка в нём не ошибка сборки, а молча потерянная настройка.
        public const string ConvertVerifyHash = "ConvertVerifyHash";
        public const string ConvertOpenFolder = "ConvertOpenFolder";
        public const string ConvertCheckDiskSpace = "ConvertCheckDiskSpace";
        public const string MergeOpenFolder = "MergeOpenFolder";
        public const string MergeCheckDiskSpace = "MergeCheckDiskSpace";
        public const string ExtractSearchFileSystems = "ExtractSearchFileSystems";
        public const string ExtractOpenFolder = "ExtractOpenFolder";
        public const string ExtractCheckDiskSpace = "ExtractCheckDiskSpace";

        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "options.txt");

        private static readonly Dictionary<string, bool> Values = new(StringComparer.Ordinal);

        static TabOptionsService() => Load();

        /// <summary>
        /// Сохранённое положение галочки или <paramref name="fallback"/>, если её
        /// никогда не трогали.
        /// </summary>
        public static bool Get(string key, bool fallback) =>
            Values.TryGetValue(key, out bool value) ? value : fallback;

        /// <summary>
        /// Запомнить положение. Пишется сразу, а не при выходе: программу закрывают
        /// и крестиком, и из диспетчера задач, и настройка, дожившая до выхода только
        /// в памяти, теряется именно в этих случаях.
        /// </summary>
        public static void Set(string key, bool value)
        {
            if (Values.TryGetValue(key, out bool current) && current == value) return;

            Values[key] = value;
            Save();
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;

                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    // Разбор через TryParse, а не сравнение с bool.TrueString. Файл этот
                    // правят блокнотом — ради того он и текстовый, — а написанное руками
                    // "true" со строчной точным сравнением не проходило и молча означало
                    // «выключено». TryParse регистр не различает и лишние пробелы прощает.
                    if (bool.TryParse(line[(split + 1)..].Trim(), out bool value))
                        Values[line[..split].Trim()] = value;

                    // Ключ, которого эта версия не знает, при чтении не отбрасывается
                    // и потом пишется обратно: файл общий, и настройка из другой версии
                    // — более новой или более старой — не должна пропадать после запуска.
                    // А вот строка с не-булевым значением здесь не наша вовсе: понять
                    // её нечем, и в словарь она не попадает (значит, и не сохранится).
                }
            }
            catch
            {
                // Не прочиталось — вкладки просто откроются со значениями по умолчанию.
            }
        }

        private static void Save()
        {
            try
            {
                string? folder = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                var lines = new List<string>(Values.Count);
                foreach (var pair in Values) lines.Add($"{pair.Key}={pair.Value}");

                File.WriteAllLines(SettingsPath, lines);
            }
            catch
            {
                // Не сохранилось — на работу это не влияет, галочка просто вернётся
                // в исходное положение при следующем запуске.
            }
        }
    }
}
