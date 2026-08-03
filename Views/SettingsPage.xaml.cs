using System.Windows.Controls;
using System.Windows.Input;
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

            // Пункт 10: handledEventsToo=true — иначе колесо мыши работает только
            // тогда, когда родительский NavigationView/Frame не пометил событие обработанным.
            AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Page_PreviewMouseWheel), true);
        }

        private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
