using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BinConverter.ViewModels;

namespace BinConverter.Views
{
    public partial class VerifyPage : Page
    {
        private readonly VerifyViewModel _viewModel = new();

        public VerifyPage()
        {
            InitializeComponent();
            DataContext = _viewModel;
        }

        private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RootScroll.ScrollToVerticalOffset(RootScroll.VerticalOffset - e.Delta);
            e.Handled = true;
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
