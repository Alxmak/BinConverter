using System.Windows;
using System.Windows.Controls;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class MergePage : Page
    {
        private readonly MergeViewModel _viewModel = TabViewModels.MergeTab;
        private readonly InputHintPopup _inputHint = new();

        public MergePage()
        {
            InitializeComponent();
            DataContext = _viewModel;
            // ViewModel живёт дольше страницы, поэтому её надо оповещать, когда
            // страница появилась и когда ушла: на появлении она догоняет то, что
            // изменилось в других разделах (язык, папка по умолчанию).
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) =>
            {
                _viewModel.Detach();
                _inputHint.Hide();
            };

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
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

        private async void SourceRow_Drop(object sender, DragEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                await _viewModel.SetSourceAsync(files[0]);
        }

        /// <summary>
        /// Вставку не отменяем: даже неполный хэш лучше оставить в поле, чтобы человек
        /// увидел, что именно приехало, и дополнил. Но сразу говорим, что это не хэш —
        /// иначе об этом сообщит только запуск сборки.
        /// </summary>
        private void ExpectedHash_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(typeof(string))) return;

            string pasted = (string)e.DataObject.GetData(typeof(string))!;
            if (Sha256Text.IsBlank(pasted) || Sha256Text.LooksValid(pasted)) return;

            _inputHint.Show(ExpectedHashTextBox,
                Strings.Format("Merge_ExpectedHashHint", Sha256Text.HexLength));
        }

        /// <summary>
        /// Здесь путь назначения — файл, а не папка, поэтому от брошенного берём только
        /// папку, а имя файла оставляем то, которое уже подобрано по цепочке.
        /// </summary>
        private void OutputRow_Drop(object sender, DragEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.SetOutputFolderKeepingFileName(folder);
        }
    }
}
