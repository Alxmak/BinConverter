using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class AboutPage : Page
    {
        private readonly AboutViewModel _viewModel = new();

        public AboutPage()
        {
            InitializeComponent();
            DataContext = _viewModel;
            // Парой: ViewModel следит за сменой языка только пока страница на экране.
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) => _viewModel.Detach();

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            e.Handled = true;
        }
    }
}
