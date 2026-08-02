using System.Windows;
using BinConverter.Services;
using Wpf.Ui.Controls;

namespace BinConverter.Views
{
    public partial class ShellWindow : FluentWindow
    {
        public ShellWindow()
        {
            InitializeComponent();
            DataContext = OperationLockService.Instance;
            Loaded += (_, _) => RootNavigation.Navigate(typeof(ConvertPage));
        }
    }
}
