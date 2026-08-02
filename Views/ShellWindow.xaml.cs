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
            WindowPlacementService.Restore(this);
            Loaded += (_, _) => RootNavigation.Navigate(typeof(ConvertPage));
            Closing += (_, _) => WindowPlacementService.Save(this);
        }
    }
}
