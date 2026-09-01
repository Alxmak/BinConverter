using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Core;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class SettingsPage : Page
    {
        // Своя ViewModel, не из TabViewModels: у «Настроек» нет состояния, которое надо
        // хранить между переходами, — они читают и пишут прямо в службы.
        //
        // Раньше из этого следовало ещё и «показанное всегда актуально»: страница
        // создавалась заново на каждый переход, а с ней и ViewModel. Теперь страницы
        // кэшируются (NavigationCacheMode в ShellWindow.xaml), и эта ViewModel живёт всё
        // время работы программы. Держится это на том, что тема, язык и три папки
        // меняются только отсюда же и только через неё. Появится второе место, которое
        // их меняет, — их придётся перечитывать в Loaded.
        private readonly SettingsViewModel _viewModel = new();

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        // Папку сюда тоже можно перетащить — подсказка в поле обещает это на всех
        // страницах, где есть строка папки. Цель — само поле, см. FileDropHelper.
        // Пока включён путь по умолчанию, поле менять нельзя, и перетаскивание тоже.
        private void ConvertFolderBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, _viewModel.CanEditConvertPath);

        private void MergeFolderBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, _viewModel.CanEditMergePath);

        private void ExtractFolderBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, _viewModel.CanEditExtractPath);

        private void ConvertFolderBox_Drop(object sender, DragEventArgs e)
        {
            string? folder = ResolveDroppedFolder(e, _viewModel.CanEditConvertPath);
            if (folder != null) _viewModel.SetCustomConvertFolder(folder);
        }

        private void MergeFolderBox_Drop(object sender, DragEventArgs e)
        {
            string? folder = ResolveDroppedFolder(e, _viewModel.CanEditMergePath);
            if (folder != null) _viewModel.SetCustomMergeFolder(folder);
        }

        private void ExtractFolderBox_Drop(object sender, DragEventArgs e)
        {
            string? folder = ResolveDroppedFolder(e, _viewModel.CanEditExtractPath);
            if (folder != null) _viewModel.SetCustomExtractFolder(folder);
        }

        private static string? ResolveDroppedFolder(DragEventArgs e, bool allowed) =>
            FileDropHelper.TakePaths(e, allowed) is string[] paths
                ? DroppedFolder.Resolve(paths[0])
                : null;
    }
}
