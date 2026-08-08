using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;

// Пространство Wpf.Ui.Controls целиком не подключаем: в нём есть свои TextBlock и Grid,
// и вместе с System.Windows.Controls они дали бы неоднозначность имён.
using MessageBox = Wpf.Ui.Controls.MessageBox;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace TweakFirmware.Services
{
    public enum DialogChoice { Primary, Secondary, Close }

    /// <summary>
    /// Все всплывающие уведомления в программе идут через этот сервис — единый
    /// современный вид (Wpf.Ui.Controls.MessageBox, в стиле Fluent) вместо
    /// устаревших системных окон Windows.
    ///
    /// Три степени важности теперь и выглядят по-разному. Раньше ShowWarningAsync
    /// и ShowErrorAsync были простыми псевдонимами ShowInfoAsync: код называл три
    /// разных случая, а на экране они были неотличимы, и вся разница жила только
    /// в тексте заголовка.
    /// </summary>
    public static class DialogService
    {
        private enum Severity { Info, Warning, Error }

        public static Task ShowInfoAsync(string title, string message) => ShowAsync(title, message, Severity.Info);
        public static Task ShowWarningAsync(string title, string message) => ShowAsync(title, message, Severity.Warning);
        public static Task ShowErrorAsync(string title, string message) => ShowAsync(title, message, Severity.Error);

        private static async Task ShowAsync(string title, string message, Severity severity)
        {
            var box = new MessageBox
            {
                Title = title,
                Content = BuildContent(message, severity),
                CloseButtonText = Strings.Get("Common_OkButton")
            };

            SetOwner(box);
            await box.ShowDialogAsync();
        }

        /// <summary>
        /// Итог с хэшем: под сообщением — подпись, сам хэш и кнопка копирования рядом.
        /// Хэш нужен не «на посмотреть»: его вписывают в поле «Ожидаемый SHA-256» при
        /// обратной сборке, а раньше 64 знака из окна оставалось только перепечатывать.
        /// </summary>
        public static async Task ShowInfoWithHashAsync(string title, string message, string hashLabel, string hash)
        {
            var box = new MessageBox
            {
                Title = title,
                Content = BuildHashContent(message, hashLabel, hash),
                CloseButtonText = Strings.Get("Common_OkButton")
            };

            SetOwner(box);
            await box.ShowDialogAsync();
        }

        private static object BuildHashContent(string message, string hashLabel, string hash)
        {
            var panel = new StackPanel();

            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            panel.Children.Add(text);

            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Подпись обычным шрифтом, хэш — моноширинным и с переносами: 64 знака
            // одной строкой в окно не влезают (см. HashDisplay).
            var hashBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
            hashBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            hashBlock.Inlines.Add(new Run(hashLabel));
            hashBlock.Inlines.Add(new LineBreak());
            hashBlock.Inlines.Add(new Run(HashDisplay.Wrap(hash))
            {
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                FontSize = 12
            });
            Grid.SetColumn(hashBlock, 0);
            row.Children.Add(hashBlock);

            var copy = BuildCopyButton(hash);
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            panel.Children.Add(row);
            return panel;
        }

        /// <summary>
        /// Копирует хэш одной строкой, без переносов, которые добавлены для показа, —
        /// вставлять нужно именно 64 знака подряд.
        /// </summary>
        private static Button BuildCopyButton(string hash)
        {
            var icon = new SymbolIcon { Symbol = SymbolRegular.Copy24, FontSize = 16 };

            var button = new Button
            {
                Content = icon,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = Strings.Get("Common_CopyHashTooltip")
            };

            button.Click += (_, _) =>
            {
                if (!ClipboardHelper.TryCopy(hash)) return;

                // Галочка вместо значка копирования — единственный отклик, который тут
                // уместен: окно короткоживущее, отдельное сообщение поверх него мешало бы.
                icon.Symbol = SymbolRegular.Checkmark24;
                button.ToolTip = Strings.Get("Common_CopiedTooltip");
            };

            return button;
        }

        /// <summary>
        /// Diagnostics: значок слева от текста. Он же единственное, что отличает
        /// сообщения по важности, — цвет берётся тот же, которым размечены итоги
        /// в самой программе (зелёный успех, красная неудача).
        /// </summary>
        private static object BuildContent(string message, Severity severity)
        {
            if (severity == Severity.Info) return message;

            var icon = new SymbolIcon
            {
                Symbol = severity == Severity.Error ? SymbolRegular.ErrorCircle24 : SymbolRegular.Warning24,
                FontSize = 20,
                Margin = new Thickness(0, 1, 10, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(severity == Severity.Error
                    ? Color.FromRgb(0xE5, 0x48, 0x4D)   // тот же красный, что у неудачи в карточках
                    : Color.FromRgb(0xF2, 0xA9, 0x3B))  // тот же оранжевый, что у паузы
            };

            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(icon);
            row.Children.Add(text);
            return row;
        }

        /// <summary>
        /// Без владельца окно сообщения центрируется по экрану, а не по программе,
        /// и может оказаться позади главного окна — что выглядит как зависание.
        /// </summary>
        private static void SetOwner(Window box)
        {
            var main = Application.Current?.MainWindow;
            if (main != null && main.IsLoaded && !ReferenceEquals(main, box)) box.Owner = main;
        }

        /// <summary>
        /// Диалог с 2-3 вариантами ответа. Primary — основное действие, Secondary — альтернативное,
        /// Close — закрыть/отменить. secondaryText можно не указывать, если вариантов только два.
        /// </summary>
        public static async Task<DialogChoice> ShowConfirmAsync(string title, string message, string primaryText, string? secondaryText, string closeText)
        {
            var box = new MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText
            };
            if (secondaryText != null) box.SecondaryButtonText = secondaryText;

            SetOwner(box);
            var result = await box.ShowDialogAsync();

            // Сравниваем по имени значения, а не по самому enum — устойчиво к точному
            // набору значений в конкретной версии библиотеки.
            return result.ToString() switch
            {
                "Primary" => DialogChoice.Primary,
                "Secondary" => DialogChoice.Secondary,
                _ => DialogChoice.Close
            };
        }
    }
}
