using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Services;
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

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        // IsEnabled="False" на карточке блокирует мышь и клавиатуру, но события
        // перетаскивания в WPF доходят и до отключённых элементов, — поэтому во время
        // сравнения перетаскивание отсекаем здесь явно.
        private void Row_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !_viewModel.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void FileA_Drop(object sender, DragEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _viewModel.SetFileA(files[0]);
        }

        private void FileB_Drop(object sender, DragEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                _viewModel.SetFileB(files[0]);
        }
    }
}
