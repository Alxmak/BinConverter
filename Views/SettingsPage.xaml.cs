using System.Windows.Controls;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class SettingsPage : Page
    {
        private readonly SettingsViewModel _viewModel = new();

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }
    }
}
