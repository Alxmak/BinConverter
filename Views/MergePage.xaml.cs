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

        // Перетаскивание принимают только поля путей — см. FileDropHelper.
        //
        // IsEnabled="False" на карточке блокирует мышь и клавиатуру, но события
        // перетаскивания в WPF доходят и до отключённых элементов, — поэтому во время
        // операции перетаскивание отсекаем здесь явно.
        private void SourceBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        private async void SourceBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] files) return;

            await _viewModel.SetSourceAsync(files[0]);
        }

        private void OutputBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        /// <summary>
        /// Здесь путь назначения — файл, а не папка, поэтому от брошенного берём только
        /// папку, а имя файла оставляем то, которое уже подобрано по цепочке.
        /// </summary>
        private void OutputBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] paths) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.SetOutputFolderKeepingFileName(folder);
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

    }
}
