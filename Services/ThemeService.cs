using System;
using System.IO;
using Wpf.Ui.Appearance;

namespace BinConverter.Services
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BinConverter", "theme.txt");

        public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;

        public static void Initialize()
        {
            CurrentMode = LoadSavedMode();
            Apply(CurrentMode);
        }

        public static void Apply(AppThemeMode mode)
        {
            CurrentMode = mode;

            ApplicationTheme theme = mode switch
            {
                AppThemeMode.Light => ApplicationTheme.Light,
                AppThemeMode.Dark => ApplicationTheme.Dark,
                _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light
            };

            ApplicationThemeManager.Apply(theme);
            SaveMode(mode);
        }

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
