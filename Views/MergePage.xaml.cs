using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BinConverter.ViewModels;

namespace BinConverter.Views
{
    public partial class MergePage : Page
    {
        private readonly MergeViewModel _viewModel = new();

        public MergePage()
        {
            InitializeComponent();
            DataContext = _viewModel;
            Unloaded += (_, _) => _viewModel.Detach();
        }

        private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void SourceRow_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void SourceRow_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _viewModel.SetSource(files[0]);
        }
    }
}
