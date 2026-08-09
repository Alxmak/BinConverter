using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Core;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class ExtractPage : Page
    {
        private readonly ExtractViewModel _viewModel = TabViewModels.ExtractTab;

        public ExtractPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // ViewModel живёт дольше страницы: разобранный список разделов должен
            // пережить переход в «Настройки» и обратно.
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) => _viewModel.Detach();

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        // Перетаскивание принимают только поля путей — см. FileDropHelper. События
        // перетаскивания доходят и до отключённых элементов, поэтому во время работы
        // отсекаем их здесь явно.
        private void SourceBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        private void SourceBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] files) return;

            _viewModel.SourcePath = files[0];
        }

        private void OutputBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        private void OutputBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] paths) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.OutputPath = folder;
        }
    }
}
