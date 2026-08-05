using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class MergePage : Page
    {
        private readonly MergeViewModel _viewModel = new();

        public MergePage()
        {
            InitializeComponent();
            DataContext = _viewModel;
            Unloaded += (_, _) => _viewModel.Detach();

            // Пункт 10: handledEventsToo=true — иначе колесо мыши работает только
            // тогда, когда родительский NavigationView/Frame не пометил событие обработанным.
            AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Page_PreviewMouseWheel), true);
        }

        private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Пункт 13/доп.: журнал скроллится изолированно от страницы — см. LogScrollHelper.
            if (LogScrollHelper.TryHandleListBoxWheel(e)) return;

            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        // IsEnabled="False" на карточке блокирует мышь и клавиатуру, но события
        // перетаскивания в WPF доходят и до отключённых элементов, — поэтому во время
        // операции перетаскивание отсекаем здесь явно.
        private void SourceRow_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_viewModel.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void SourceRow_Drop(object sender, DragEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _viewModel.SetSource(files[0]);
        }
    }
}
