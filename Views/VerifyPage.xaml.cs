using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class VerifyPage : Page
    {
        private readonly VerifyViewModel _viewModel = TabViewModels.VerifyTab;

        public VerifyPage()
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

        // Перетаскивание принимают только поля путей — см. FileDropHelper.
        //
        // IsEnabled="False" на карточке блокирует мышь и клавиатуру, но события
        // перетаскивания в WPF доходят и до отключённых элементов, — поэтому во время
        // сравнения перетаскивание отсекаем здесь явно.
        private void FileBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        /// <summary>
        /// Строка, в поле которой бросили файл, известна по DataContext этого поля.
        /// Если файлов перетащили несколько, они раскладываются с этой строки и вниз —
        /// так пять дампов добавляются одним движением, а не пятью.
        /// </summary>
        private void FileBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] files) return;
            if (sender is not FrameworkElement box || box.DataContext is not VerifyFileSlot slot) return;

            _viewModel.SetPathsFrom(slot, files);
        }
    }
}
