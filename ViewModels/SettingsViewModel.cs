using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        [ObservableProperty]
        private AppThemeMode currentTheme;

        [ObservableProperty]
        private string selectedLanguage;

        // Пункт 5/7: «Путь по умолчанию» переключается отдельно для «Конвертирования»
        // и для «Сборки файла».
        [ObservableProperty] private bool useDefaultConvertPath;
        [ObservableProperty] private bool useDefaultMergePath;

        [ObservableProperty] private string convertFolder = "";
        [ObservableProperty] private string mergeFolder = "";

        public bool CanEditConvertPath => !UseDefaultConvertPath;
        public bool CanEditMergePath => !UseDefaultMergePath;

        public SettingsViewModel()
        {
            currentTheme = ThemeService.CurrentMode;
            selectedLanguage = LocalizationService.Instance.CurrentLanguage;
            useDefaultConvertPath = OutputPathSettingsService.UseDefaultConvertPath;
            useDefaultMergePath = OutputPathSettingsService.UseDefaultMergePath;
            RefreshFolderDisplays();
        }

        partial void OnUseDefaultConvertPathChanged(bool value)
        {
            OutputPathSettingsService.SetUseDefaultConvertPath(value);
            OnPropertyChanged(nameof(CanEditConvertPath));
            RefreshFolderDisplays();
        }

        partial void OnUseDefaultMergePathChanged(bool value)
        {
            OutputPathSettingsService.SetUseDefaultMergePath(value);
            OnPropertyChanged(nameof(CanEditMergePath));
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
        private void SetLanguage(string code)
        {
            SelectedLanguage = code;
            LocalizationService.Instance.SetLanguage(code);
        }

        [RelayCommand]
        private void BrowseConvertFolder()
        {
            var dlg = new OpenFolderDialog { Title = Strings.Get("Settings_ChooseConvertFolderTitle") };
            if (dlg.ShowDialog() == true)
            {
                OutputPathSettingsService.SetCustomConvertFolder(dlg.FolderName);
                RefreshFolderDisplays();
            }
        }

        [RelayCommand]
        private void BrowseMergeFolder()
        {
            var dlg = new OpenFolderDialog { Title = Strings.Get("Settings_ChooseMergeFolderTitle") };
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
            MessageBox.Show(Strings.Get("Settings_CacheClearedMessage"), Strings.Get("Settings_CacheClearedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
