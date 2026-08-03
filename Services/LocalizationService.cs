using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Обёртка над TweakFirmware.Core.Localization.Strings для WPF: хранит текущий язык,
    /// сохраняет выбор между запусками и — самое важное — даёт индексатор, привязка к
    /// которому (через разметку {loc:Loc Key}, см. LocExtension) обновляется на лету при
    /// переключении языка, без перезапуска приложения.
    /// </summary>
    public partial class LocalizationService : ObservableObject
    {
        public static LocalizationService Instance { get; } = new();

        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "language.txt");

        [ObservableProperty] private string currentLanguage = Strings.DefaultLanguage;

        /// <summary>Индексатор для XAML-привязок вида {Binding Source={x:Static ...Instance}, Path=[Key]}.</summary>
        public string this[string key] => Strings.Get(key);

        private LocalizationService() { }

        /// <summary>Вызывается один раз при старте приложения — до создания окна.</summary>
        public void Initialize()
        {
            CurrentLanguage = LoadSavedLanguage();
            Apply(CurrentLanguage, persist: false);
        }

        /// <summary>Вызывается при выборе языка в настройках.</summary>
        public void SetLanguage(string code) => Apply(code, persist: true);

        private void Apply(string code, bool persist)
        {
            string normalized = code == "en" ? "en" : "ru";
            CurrentLanguage = normalized;
            Strings.SetLanguage(normalized);

            // И CurrentCulture (форматирование чисел/дат — разделители разрядов и т.п.),
            // и CurrentUICulture (на случай если что-то в будущем будет читать её напрямую) —
            // для текущего потока и как умолчание для новых потоков (Task.Run в конвертере/
            // сборке идёт в фоновых потоках, которые иначе унаследуют системную культуру).
            var culture = new CultureInfo(normalized == "en" ? "en-US" : "ru-RU");
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            if (persist) SaveLanguage(normalized);

            // WPF слушает именно это специальное имя свойства, чтобы обновить все
            // привязки к индексатору — в т.ч. те, что созданы через {loc:Loc ...}.
            OnPropertyChanged(Binding.IndexerName);
        }

        private static string LoadSavedLanguage()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string saved = File.ReadAllText(SettingsPath).Trim();
                    if (saved is "en" or "ru") return saved;
                }
            }
            catch { /* используем язык по умолчанию, если не удалось прочитать */ }
            return Strings.DefaultLanguage;
        }

        private static void SaveLanguage(string code)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, code);
            }
            catch { /* сохранение языка не критично для работы программы */ }
        }
    }
}
