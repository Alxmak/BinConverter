using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class ConvertPage : Page
    {
        private readonly ConvertViewModel _viewModel = TabViewModels.ConvertTab;
        private Popup? _toastPopup;
        private DispatcherTimer? _toastTimer;

        public ConvertPage()
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

        // Пункт 15: drag&drop работает только над строкой выбора исходного файла,
        // а не над всей страницей.
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

        private void ShowInvalidInputToast(FrameworkElement anchor, string message)
        {
            if (_toastPopup == null)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x3F)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6)
                };
                var text = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
                border.Child = text;

                _toastPopup = new Popup
                {
                    Child = border,
                    Placement = PlacementMode.Bottom,
                    PlacementTarget = anchor,
                    AllowsTransparency = true,
                    StaysOpen = true,
                    VerticalOffset = 4
                };
            }

            ((TextBlock)((Border)_toastPopup.Child).Child).Text = message;
            _toastPopup.PlacementTarget = anchor;
            _toastPopup.IsOpen = true;

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastPopup!.IsOpen = false;
                _toastTimer!.Stop();
            };
            _toastTimer.Start();
        }
    }
}
