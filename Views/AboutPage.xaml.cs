using System.Windows.Controls;
using System.Windows.Navigation;
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

        /// <summary>
        /// Ссылку открывает ViewModel — тем же путём, что и кнопка «GitHub». Раньше
        /// Process.Start стоял прямо здесь и без перехвата: на машине, где браузер
        /// не назначен, он бросает исключение, и оно уходило наружу необработанным.
        /// </summary>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            // Результат намеренно не ждём: обработчик события возвращает void, а всё,
            // что может пойти не так, разобрано внутри OpenLinkAsync.
            _ = _viewModel.OpenLinkAsync(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
