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

            // Пункт 4: Mica — только Windows 11 (DWM-эффект появился именно там). На более
            // старых системах (в т.ч. Windows 10) WPF-UI поддерживает Acrylic начиная с Windows 7,
            // поэтому там используем его вместо простого фона. Скруглённые углы окна — тоже
            // возможность DWM из Windows 11, на Windows 10 не имеет смысла и не включаем её.
            bool isWindows11OrNewer = Environment.OSVersion.Version.Build >= 22000;
            WindowBackdropType = isWindows11OrNewer ? WindowBackdropType.Mica : WindowBackdropType.Acrylic;
            WindowCornerPreference = isWindows11OrNewer ? WindowCornerPreference.Round : WindowCornerPreference.Default;

            WindowPlacementService.Restore(this);
            Loaded += (_, _) => RootNavigation.Navigate(typeof(ConvertPage));
            Closing += (_, _) => WindowPlacementService.Save(this);
        }
    }
}
