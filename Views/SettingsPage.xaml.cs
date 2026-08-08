using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Core;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class SettingsPage : Page
    {
        private readonly SettingsViewModel _viewModel = new();

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        // Папку сюда тоже можно перетащить — подсказка в поле обещает это на всех
        // страницах, где есть строка папки. Пока включён путь по умолчанию, поле
        // менять нельзя: события перетаскивания доходят и до отключённых элементов,
        // поэтому проверяем это сами (тот же случай, что на рабочих вкладках).
        private void ConvertFolderRow_DragOver(object sender, DragEventArgs e) =>
            SetFolderDropEffect(e, _viewModel.CanEditConvertPath);

        private void MergeFolderRow_DragOver(object sender, DragEventArgs e) =>
            SetFolderDropEffect(e, _viewModel.CanEditMergePath);

        private static void SetFolderDropEffect(DragEventArgs e, bool allowed)
        {
            e.Effects = allowed && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void ConvertFolderRow_Drop(object sender, DragEventArgs e)
        {
            string? folder = ResolveDroppedFolder(e, _viewModel.CanEditConvertPath);
            if (folder != null) _viewModel.SetCustomConvertFolder(folder);
        }

        private void MergeFolderRow_Drop(object sender, DragEventArgs e)
        {
            string? folder = ResolveDroppedFolder(e, _viewModel.CanEditMergePath);
            if (folder != null) _viewModel.SetCustomMergeFolder(folder);
        }

        private static string? ResolveDroppedFolder(DragEventArgs e, bool allowed)
        {
            if (!allowed) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return null;

            return DroppedFolder.Resolve(paths[0]);
        }
    }
}
