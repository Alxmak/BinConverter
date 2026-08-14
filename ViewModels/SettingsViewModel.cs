using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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

        // Пункт 5/7: «Путь по умолчанию» переключается отдельно для «Конвертирования»,
        // «Сборки файла» и «Извлечения разделов».
        [ObservableProperty] private bool useDefaultConvertPath;
        [ObservableProperty] private bool useDefaultMergePath;
        [ObservableProperty] private bool useDefaultExtractPath;

        [ObservableProperty] private string convertFolder = "";
        [ObservableProperty] private string mergeFolder = "";
        [ObservableProperty] private string extractFolder = "";

        public bool CanEditConvertPath => !UseDefaultConvertPath;
        public bool CanEditMergePath => !UseDefaultMergePath;
        public bool CanEditExtractPath => !UseDefaultExtractPath;

        public SettingsViewModel()
        {
            currentTheme = ThemeService.CurrentMode;
            selectedLanguage = LocalizationService.Instance.CurrentLanguage;
            useDefaultConvertPath = OutputPathSettingsService.UseDefaultConvertPath;
            useDefaultMergePath = OutputPathSettingsService.UseDefaultMergePath;
            useDefaultExtractPath = OutputPathSettingsService.UseDefaultExtractPath;
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

        partial void OnUseDefaultExtractPathChanged(bool value)
        {
            OutputPathSettingsService.SetUseDefaultExtractPath(value);
            OnPropertyChanged(nameof(CanEditExtractPath));
            RefreshFolderDisplays();
        }

        private void RefreshFolderDisplays()
        {
            ConvertFolder = OutputPathSettingsService.GetConvertFolder();
            MergeFolder = OutputPathSettingsService.GetMergeFolder();
            ExtractFolder = OutputPathSettingsService.GetExtractFolder();
        }

        // Пункт: поля путей теперь редактируются свободно (можно вставлять и печатать).
        // Проверка "не в режиме по умолчанию" — иначе показ дефолтного пути при включённой
        // галочке "Путь по умолчанию" затирал бы ранее сохранённый пользовательский путь.
        partial void OnConvertFolderChanged(string value)
        {
            if (!UseDefaultConvertPath) OutputPathSettingsService.SetCustomConvertFolder(value);
        }

        partial void OnMergeFolderChanged(string value)
        {
            if (!UseDefaultMergePath) OutputPathSettingsService.SetCustomMergeFolder(value);
        }

        partial void OnExtractFolderChanged(string value)
        {
            if (!UseDefaultExtractPath) OutputPathSettingsService.SetCustomExtractFolder(value);
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
            if (dlg.ShowDialog() == true) SetCustomConvertFolder(dlg.FolderName);
        }

        [RelayCommand]
        private void BrowseMergeFolder()
        {
            var dlg = new OpenFolderDialog { Title = Strings.Get("Settings_ChooseMergeFolderTitle") };
            if (dlg.ShowDialog() == true) SetCustomMergeFolder(dlg.FolderName);
        }

        [RelayCommand]
        private void BrowseExtractFolder()
        {
            var dlg = new OpenFolderDialog { Title = Strings.Get("Settings_ChooseExtractFolderTitle") };
            if (dlg.ShowDialog() == true) SetCustomExtractFolder(dlg.FolderName);
        }

        /// <summary>Используется и кнопкой "Обзор...", и вставкой пути из буфера обмена.</summary>
        public void SetCustomConvertFolder(string path)
        {
            OutputPathSettingsService.SetCustomConvertFolder(path);
            RefreshFolderDisplays();
        }

        public void SetCustomMergeFolder(string path)
        {
            OutputPathSettingsService.SetCustomMergeFolder(path);
            RefreshFolderDisplays();
        }

        public void SetCustomExtractFolder(string path)
        {
            OutputPathSettingsService.SetCustomExtractFolder(path);
            RefreshFolderDisplays();
        }

        /// <summary>
        /// Чистит журнал — и файл на диске, и то, что показано в карточке «Журнал».
        /// Раньше вызывался AppLogger.ClearLog напрямую: файл становился пустым, а на
        /// экране оставались все прежние строки, и «Сохранить» отдавал файл, не
        /// совпадающий с тем, что человек видит. LogService.Clear делает и то, и другое.
        /// </summary>
        [RelayCommand]
        private async Task ClearLogAsync()
        {
            LogService.Clear();
            await DialogService.ShowInfoAsync(Strings.Get("Common_DoneTitle"), Strings.Get("Settings_LogClearedMessage"));
        }
    }
}
