using System;
using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TweakFirmware.Services
{
    public enum AppThemeMode { Light, Dark, System }

    /// <summary>
    /// Обёртка над Wpf.Ui.Appearance.ApplicationThemeManager: сохраняет выбор темы между
    /// запусками и — что важнее — доводит применение темы до самого окна.
    ///
    /// Тема состоит из двух независимых частей:
    ///   1) словарь ресурсов (цвета кистей) — общий для приложения;
    ///   2) оформление самого окна — тёмный режим DWM и подложка (Mica/Acrylic).
    ///
    /// ApplicationThemeManager.Apply делает вторую часть только для
    /// UiApplication.Current.MainWindow и только если окно уже существует:
    ///
    ///     if (UiApplication.Current.MainWindow is Window mainWindow)
    ///         WindowBackgroundManager.UpdateBackground(mainWindow, applicationTheme, backgroundEffect);
    ///
    /// Тему мы применяем на старте, ДО создания окна (иначе видно мигание светлой темы),
    /// поэтому MainWindow там ещё null и вторая часть молча пропускалась. На светлой теме
    /// это незаметно, а на тёмной окно оставалось без тёмного режима DWM: кисти текста
    /// уже почти белые, а подложка — светло-серая, отсюда нечитаемый текст после
    /// перезапуска с сохранённой тёмной темой. Поэтому вторую часть вызываем сами —
    /// см. <see cref="ApplyToWindow"/>.
    /// </summary>
    public static class ThemeService
    {
        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "theme.txt");

        public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;

        /// <summary>
        /// Подложка окна: Mica — эффект DWM из Windows 11, на более старых системах
        /// (включая Windows 10) используем Acrylic. Единственное место, где это решается, —
        /// раньше значение дублировалось в ShellWindow, а ApplicationThemeManager.Apply
        /// вообще перебивал его своим значением по умолчанию (Mica) при каждом
        /// переключении темы, даже на Windows 10, где Mica не поддерживается.
        /// </summary>
        public static WindowBackdropType PreferredBackdrop =>
            Environment.OSVersion.Version.Build >= 22000
                ? WindowBackdropType.Mica
                : WindowBackdropType.Acrylic;

        public static void Initialize()
        {
            CurrentMode = LoadSavedMode();
            ApplicationThemeManager.Apply(Resolve(CurrentMode), PreferredBackdrop);
            SaveMode(CurrentMode);
        }

        // Одно применение на переключение — и ничего лишнего. Раньше здесь стоял обход:
        // тема сначала применялась противоположная, потом нужная, потому что при совпадении
        // словаря ApplicationThemeManager выходит раньше времени и не доводил оформление до
        // окна. Само оформление окна мы теперь делаем сами (см. ApplyToWindow), так что обход
        // стал не нужен — а платой за него было мерцание: на каждый клик выходили две полные
        // смены словаря (одна в чужую тему) и три снятия/наложения подложки окна.
        // Побочный плюс: повторный выбор уже активной темы теперь ничего не перерисовывает.
        public static void Apply(AppThemeMode mode)
        {
            CurrentMode = mode;
            ApplicationThemeManager.Apply(Resolve(mode), PreferredBackdrop);
            SaveMode(mode);
        }

        /// <summary>
        /// Оформление окна под текущую тему: тёмный режим DWM (заголовок и рамка) и подложка.
        /// Нужно только на старте: тему выставляем до создания окна, и ApplicationThemeManager
        /// этот шаг пропускает, потому что окна ещё нет. При переключении темы на живом окне
        /// он делает его сам — здесь второй раз вызывать не надо, иначе окно мигнёт.
        /// </summary>
        public static void ApplyToWindow(Window? window)
        {
            if (window is null) return;
            WindowBackgroundManager.UpdateBackground(window, Resolve(CurrentMode), PreferredBackdrop);
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
