using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class VerifyPage : Page
    {
        private readonly VerifyViewModel _viewModel = new();

        public VerifyPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // Пункт 10: handledEventsToo=true — иначе колесо мыши работает только
            // тогда, когда родительский NavigationView/Frame не пометил событие обработанным.
            AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Page_PreviewMouseWheel), true);
        }

        private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Пункт 13: над журналом должен скроллиться сам список, а не вся страница.
            if (IsWithinListBox(e.OriginalSource as DependencyObject)) return;

            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private static bool IsWithinListBox(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ListBox) return true;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }

        private void Row_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void FileA_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _viewModel.SetFileA(files[0]);
        }

        private void FileB_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _viewModel.SetFileB(files[0]);
        }
    }
}
