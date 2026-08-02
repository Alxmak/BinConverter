using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        [ObservableProperty]
        private AppThemeMode currentTheme;

        // Пункт 13: задел на будущую локализацию через .resx — сама настройка пока
        // ни на что не влияет, только хранит выбор.
        [ObservableProperty]
        private string selectedLanguage = "ru";

        // Пункт 7: папки по умолчанию для «Конвертирования» и «Сборки файла».
        [ObservableProperty]
        private bool useDefaultPaths;

        [ObservableProperty] private string convertFolder = "";
        [ObservableProperty] private string mergeFolder = "";

        public bool CanEditCustomPaths => !UseDefaultPaths;

        public SettingsViewModel()
        {
            currentTheme = ThemeService.CurrentMode;
            useDefaultPaths = OutputPathSettingsService.UseDefaultPaths;
            RefreshFolderDisplays();
        }

        partial void OnUseDefaultPathsChanged(bool value)
        {
            OutputPathSettingsService.SetUseDefaultPaths(value);
            OnPropertyChanged(nameof(CanEditCustomPaths));
            RefreshFolderDisplays();
        }

        private void RefreshFolderDisplays()
        {
            ConvertFolder = OutputPathSettingsService.GetConvertFolder();
            MergeFolder = OutputPathSettingsService.GetMergeFolder();
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
        private void BrowseConvertFolder()
        {
            var dlg = new OpenFolderDialog { Title = "Папка по умолчанию для конвертирования" };
            if (dlg.ShowDialog() == true)
            {
                OutputPathSettingsService.SetCustomConvertFolder(dlg.FolderName);
                RefreshFolderDisplays();
            }
        }

        [RelayCommand]
        private void BrowseMergeFolder()
        {
            var dlg = new OpenFolderDialog { Title = "Папка по умолчанию для сборки файла" };
            if (dlg.ShowDialog() == true)
            {
                OutputPathSettingsService.SetCustomMergeFolder(dlg.FolderName);
                RefreshFolderDisplays();
            }
        }

        [RelayCommand]
        private void ClearCache()
        {
            AppLogger.ClearLog();
            MessageBox.Show("Временные файлы и журналы очищены.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
