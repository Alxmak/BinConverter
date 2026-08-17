using System.Windows.Controls;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class AboutPage : Page
    {
        private readonly AboutViewModel _viewModel = TabViewModels.AboutTab;

        public AboutPage()
        {
            InitializeComponent();
            DataContext = _viewModel;
            // ViewModel живёт дольше страницы, поэтому её надо оповещать, когда
            // страница появилась и когда ушла: на появлении она догоняет то, что
            // изменилось в других разделах (язык, папка по умолчанию).
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) => _viewModel.Detach();

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }
    }
}
