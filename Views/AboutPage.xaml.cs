using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
            DataContext = new AboutViewModel();

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            e.Handled = true;
        }
    }
}
