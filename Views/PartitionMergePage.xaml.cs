using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Core;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class PartitionMergePage : Page
    {
        private readonly PartitionMergeViewModel _viewModel = TabViewModels.PartitionMergeTab;

        public PartitionMergePage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // ViewModel живёт дольше страницы, поэтому её надо оповещать, когда страница
            // появилась и когда ушла: на появлении она догоняет то, что изменилось
            // в других разделах, и заново осматривает папку.
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) => _viewModel.Detach();

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        // Перетаскивание принимают только поля путей — см. FileDropHelper. События
        // перетаскивания доходят и до отключённых элементов, поэтому во время работы
        // отсекаем их здесь явно.
        private void SourceBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        /// <summary>Здесь источник — папка, поэтому от брошенного берём именно её.</summary>
        private void SourceBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] paths) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.SourceFolder = folder;
        }

        private void OutputBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        /// <summary>
        /// Путь назначения — файл, а не папка, поэтому от брошенного берём только папку,
        /// а имя оставляем то, которое уже подобрано. То же, что в «Сборке файла».
        /// </summary>
        private void OutputBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] paths) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.SetOutputFolderKeepingFileName(folder);
        }
    }
}
