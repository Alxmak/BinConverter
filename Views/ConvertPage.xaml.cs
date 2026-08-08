using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class ConvertPage : Page
    {
        private readonly ConvertViewModel _viewModel = TabViewModels.ConvertTab;
        private readonly InputHintPopup _inputHint = new();

        public ConvertPage()
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
                // Попап живёт в своём окне и на закрытой странице остался бы висеть.
                _inputHint.Hide();
            };

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        // Перетаскивание принимают только поля путей — почему именно так и почему через
        // Preview-события, см. FileDropHelper.
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
        /// Бросили файл, а не папку — берём папку, в которой он лежит: тащить из
        /// проводника файл проще, чем прицелиться в саму папку.
        /// </summary>
        private void OutputBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] paths) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.SetOutputFolder(folder);
        }

        // ============================= Только цифры в поле лимита =============================

        // Пункт 3: показываем байты с разбивкой по разрядам, как в "Общая информация"
        // (SizeFormatHelper тоже использует "N0"). Разбираем/собираем значение вручную,
        // чтобы курсор не "прыгал" при появлении новых разделителей.
        private bool _formattingLimit;

        private void LimitTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_formattingLimit) return;
            var tb = (TextBox)sender;

            string digits = new string(tb.Text.Where(char.IsDigit).ToArray());
            string formatted = digits.Length > 0 && long.TryParse(digits, out var n) ? n.ToString("N0") : digits;

            if (formatted == tb.Text) return;

            int digitsBeforeCaret = tb.Text.Take(tb.CaretIndex).Count(char.IsDigit);

            _formattingLimit = true;
            tb.Text = formatted;

            int newCaret = 0, seen = 0;
            while (newCaret < formatted.Length && seen < digitsBeforeCaret)
            {
                if (char.IsDigit(formatted[newCaret])) seen++;
                newCaret++;
            }
            tb.CaretIndex = newCaret;
            _formattingLimit = false;
        }

        private void LimitTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!e.Text.All(char.IsDigit))
            {
                e.Handled = true;
                ShowInvalidInputToast((TextBox)sender, Strings.Get("Common_DigitsOnly"));
            }
        }

        private void LimitTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string))!;
                if (!text.All(char.IsDigit))
                {
                    e.CancelCommand();
                    ShowInvalidInputToast((TextBox)sender, Strings.Get("Common_DigitsOnly"));
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void ShowInvalidInputToast(FrameworkElement anchor, string message) =>
            _inputHint.Show(anchor, message);
    }
}
