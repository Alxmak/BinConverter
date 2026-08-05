using System;
using System.Windows;
using TweakFirmware.Services;
using Wpf.Ui.Controls;

namespace TweakFirmware.Views
{
    public partial class ShellWindow : FluentWindow
    {
        public ShellWindow()
        {
            InitializeComponent();
            DataContext = OperationLockService.Instance;

            // Mica — эффект DWM из Windows 11; на более старых системах (в т.ч. Windows 10)
            // используем Acrylic. Выбор подложки живёт в ThemeService, чтобы он не разъезжался
            // с тем, что применяется при переключении темы. Скруглённые углы окна — тоже
            // возможность DWM из Windows 11, на Windows 10 не имеет смысла и не включаем её.
            bool isWindows11OrNewer = Environment.OSVersion.Version.Build >= 22000;
            WindowBackdropType = ThemeService.PreferredBackdrop;
            WindowCornerPreference = isWindows11OrNewer ? WindowCornerPreference.Round : WindowCornerPreference.Default;

            // Тёмный режим самого окна (заголовок, рамка) на старте не применяется: тему
            // выставляем до создания окна, а WPF-UI делает эту часть только для уже
            // существующего MainWindow. Здесь дескриптор окна уже есть — доводим сами,
            // иначе на тёмной теме окно остаётся светлым, а текст на нём — почти белым.
            SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);

            WindowPlacementService.Restore(this);
            Loaded += (_, _) => RootNavigation.Navigate(typeof(ConvertPage));
            Closing += (_, _) => WindowPlacementService.Save(this);
        }
    }
}
