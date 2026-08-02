using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
            DataContext = new AboutViewModel();

            // Пункт 10: handledEventsToo=true — иначе колесо мыши работает только
            // тогда, когда родительский NavigationView/Frame не пометил событие обработанным.
            AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Page_PreviewMouseWheel), true);
        }

        private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            e.Handled = true;
        }
    }
}
