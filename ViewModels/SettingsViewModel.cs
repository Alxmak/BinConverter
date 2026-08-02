using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BinConverter.Core;
using BinConverter.Services;

namespace BinConverter.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        [ObservableProperty]
        private AppThemeMode currentTheme;

        // Пункт 13: задел на будущую локализацию через .resx — сама настройка пока
        // ни на что не влияет, только хранит выбор.
        [ObservableProperty]
        private string selectedLanguage = "ru";

        public SettingsViewModel()
        {
            currentTheme = ThemeService.CurrentMode;
        }

        [RelayCommand]
        private void SetTheme(string modeName)
        {
            if (Enum.TryParse<AppThemeMode>(modeName, out var mode))
            {
                CurrentTheme = mode;
                ThemeService.Apply(mode);
            }
        }

        [RelayCommand]
        private void SetLanguage(string code) => SelectedLanguage = code;

        [RelayCommand]
        private void ClearCache()
        {
            AppLogger.ClearLog();
            MessageBox.Show("Временные файлы и журналы очищены.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
