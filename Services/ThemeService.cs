using System;
using System.IO;
using Wpf.Ui.Appearance;

namespace TweakFirmware.Services
{
    public enum AppThemeMode { Light, Dark, System }

    /// <summary>
    /// Тонкая обёртка над Wpf.Ui.Appearance.ApplicationThemeManager (сама переключает
    /// тему всех WPF-UI контролов мгновенно) — добавляет только сохранение выбора
    /// пользователя между запусками программы.
    /// </summary>
    public static class ThemeService
    {
        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "theme.txt");

        public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;

        public static void Initialize()
        {
            CurrentMode = LoadSavedMode();
            ApplicationTheme resolved = Resolve(CurrentMode);

            // Пункт 2: App.xaml статически подключает тёмный словарь ресурсов (Theme="Dark").
            // Если сохранённая тема тоже тёмная, ApplicationThemeManager считает, что тема
            // не изменилась, и не проводит применение полностью — часть текста остаётся
            // нечитаемой (чёрной) до первого ручного переключения темы. Поэтому сначала
            // "переключаем" на противоположную тему, а затем — на нужную; это то же самое,
            // что временное ручное переключение, которым пользователи обходили баг вручную.
            ApplicationThemeManager.Apply(resolved == ApplicationTheme.Dark ? ApplicationTheme.Light : ApplicationTheme.Dark);
            ApplicationThemeManager.Apply(resolved);
            SaveMode(CurrentMode);
        }

        public static void Apply(AppThemeMode mode)
        {
            CurrentMode = mode;
            ApplicationThemeManager.Apply(Resolve(mode));
            SaveMode(mode);
        }

        private static ApplicationTheme Resolve(AppThemeMode mode) => mode switch
        {
            AppThemeMode.Light => ApplicationTheme.Light,
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light
        };

        private static AppThemeMode LoadSavedMode()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string saved = File.ReadAllText(SettingsPath).Trim();
                    if (Enum.TryParse<AppThemeMode>(saved, out var mode))
                        return mode;
                }
            }
            catch { /* используем значение по умолчанию, если не удалось прочитать */ }

            return AppThemeMode.System;
        }

        private static void SaveMode(AppThemeMode mode)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, mode.ToString());
            }
            catch { /* сохранение темы не критично для работы программы */ }
        }
    }
}
