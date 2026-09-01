using System.Collections.Generic;
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

        private static async Task ShowAsync(string title, string message, Severity severity, HashRow[]? hashes = null)
        {
            var box = new MessageBox
            {
                Title = title,
                Content = BuildContent(message, severity, hashes),
                CloseButtonText = Strings.Get("Common_OkButton")
            };

            SetOwner(box);
            await box.ShowDialogAsync();
        }

        /// <summary>
        /// Подпись и сам хэш — одна строка итога в окне. <paramref name="Note"/> — то,
        /// что показывается под хэшем: в сверке это имя файла, которому он принадлежит.
        /// </summary>
        public readonly record struct HashRow(string Label, string Hash, string Note = "");

        /// <summary>
        /// Текстовая строка для окна. Весь текст диалогов идёт через неё.
        ///
        /// Выравнивание задаётся явно, и это не придирка: MessageBox из WPF-UI кладёт
        /// в ресурсы своего ContentPresenter неявный стиль TextBlock с
        /// TextAlignment="Justify". Он достаётся всему, что мы кладём в окно, и текст
        /// растягивается по ширине. На обычной фразе это почти незаметно, а на пути
        /// к папке — «C:\Users\...\New Firmware Files\Extracted Partitions\...» —
        /// превращалось в дыры по несколько пробелов между кусками пути: имена папок
        /// с пробелами дают точки переноса, и выравнивание растаскивало их по всей
        /// ширине окна.
        /// </summary>
        private static TextBlock BuildText(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            return block;
        }

        /// <summary>
        /// Итог с хэшами: под сообщением у каждого своя подпись и кнопка копирования.
        /// Хэш нужен не «на посмотреть» — его вписывают в поле «Ожидаемый SHA-256» при
        /// обратной сборке, а раньше 64 знака из окна оставалось только перепечатывать.
        /// </summary>
        public static Task ShowInfoWithHashesAsync(string title, string message, params HashRow[] hashes) =>
            ShowAsync(title, message, Severity.Info, hashes);

        /// <summary>То же для расхождения хэшей: там их два, и оба нужны в буфере обмена
        /// не меньше — именно с ними идут разбираться, почему результат не совпал.</summary>
        public static Task ShowErrorWithHashesAsync(string title, string message, params HashRow[] hashes) =>
            ShowAsync(title, message, Severity.Error, hashes);

        /// <summary>
        /// Строка хэша: подпись, под ней с отступом сам хэш, а кнопка копирования —
        /// на уровне хэша, а не подписи. Ниже — необязательное пояснение
        /// (<see cref="HashRow.Note"/>).
        /// </summary>
        private static UIElement BuildHashRow(HashRow row)
        {
            var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

            block.Children.Add(BuildText(row.Label));

            var line = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Моноширинный и с переносами: 64 знака одной строкой в окно не влезают
            // (см. HashDisplay).
            var hash = BuildText(HashDisplay.Wrap(row.Hash));
            hash.FontFamily = new FontFamily("Cascadia Code, Consolas");
            hash.FontSize = 12;
            Grid.SetColumn(hash, 0);
            line.Children.Add(hash);

            var copy = BuildCopyButton(row.Hash);
            Grid.SetColumn(copy, 1);
            line.Children.Add(copy);

            block.Children.Add(line);

            // Пояснение идёт под хэшем, а не в подпись над ним: в сверке окно повторяет
            // карточку результата на странице, а там порядок «что за строка → хэш →
            // какой это файл». Перестановка читалась бы как другой ответ.
            if (!string.IsNullOrEmpty(row.Note))
            {
                var note = BuildText(row.Note);
                note.Margin = new Thickness(0, 6, 0, 0);
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                block.Children.Add(note);
            }

            return block;
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
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = Strings.Get("Common_CopyHashTooltip"),
                // Кнопка из одного значка: стандартная раскраска WPF-UI в тёмной теме
                // даёт почти невидимый квадрат — см. IconButtonStyle в AppStyles.
                Style = (Style)Application.Current.FindResource("IconButtonStyle")
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

        private static object BuildContent(string message, Severity severity, HashRow[]? hashes)
        {
            // Простое сообщение раньше отдавалось в окно голой строкой — «пусть библиотека
            // нарисует его сама». Рисовала она его по-своему: строку разворачивает
            // в TextBlock её собственный ContentPresenter, а у него в ресурсах лежит
            // неявный стиль с TextAlignment="Justify". Текст растягивался по ширине окна,
            // и на пути к папке это превращалось в дыры по несколько пробелов между
            // кусками пути — ровно там, где в именах папок есть пробелы. Свой TextBlock
            // выравнивание задаёт явно, поэтому исключений больше нет: весь текст окон
            // собирается здесь.
            var panel = new StackPanel();
            panel.Children.Add(BuildMessageRow(message, severity));

            if (hashes != null)
                foreach (var row in hashes) panel.Children.Add(BuildHashRow(row));

            return panel;
        }

        /// <summary>
        /// Значок слева от текста. Он же единственное, что отличает сообщения
        /// по важности, — цвет берётся тот же, которым размечены итоги в самой
        /// программе (красная неудача, оранжевое предупреждение).
        /// </summary>
        private static UIElement BuildMessageRow(string message, Severity severity)
        {
            var text = BuildText(message);

            if (severity == Severity.Info) return text;

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
        ///
        /// Здесь же зовём человека обратно, если он смотрит не сюда. Место выбрано
        /// не случайно: диалог — единственный момент, когда программа перестаёт работать
        /// и ждёт ответа. Нарезка на час заканчивалась окном с итогом, вопрос о размере
        /// страницы NAND возникает посреди разбора — и то, и другое висело незамеченным,
        /// пока свёрнутое окно молчало. Мигание кнопки в панели задач и есть
        /// предусмотренный в Windows способ позвать, не лезя поверх чужой работы.
        /// Окно на переднем плане не мигает, поэтому подтверждения, вызванные щелчком
        /// (отмена, закрытие), сюда не попадают.
        /// </summary>
        private static void SetOwner(Window box)
        {
            var main = Application.Current?.MainWindow;
            if (main == null || !main.IsLoaded || ReferenceEquals(main, box)) return;

            box.Owner = main;
            WindowAttention.CallIfHidden(main);
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
                // Не сама строка, а TextBlock: голую строку окно оформляет своим
                // неявным стилем и растягивает по ширине — см. BuildText.
                Content = BuildText(message),
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

        /// <summary>
        /// Выбор одного варианта из списка. Нужен разбору дампа: когда размер страницы
        /// NAND не определился сам, его приходится спрашивать, а вариантов девять —
        /// кнопками их не покажешь.
        ///
        /// Возвращает номер выбранного варианта или <c>null</c>, если человек отказался.
        /// </summary>
        public static async Task<int?> AskChoiceAsync(string title, string message, IReadOnlyList<string> options)
        {
            if (options.Count == 0) return null;

            var list = new ComboBox { SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 0) };
            foreach (string option in options) list.Items.Add(option);

            var panel = new StackPanel();
            panel.Children.Add(BuildText(message));
            panel.Children.Add(list);

            var box = new MessageBox
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = Strings.Get("Common_OkButton"),
                CloseButtonText = Strings.Get("Common_CancelButton")
            };

            SetOwner(box);
            var result = await box.ShowDialogAsync();

            return result.ToString() == "Primary" ? list.SelectedIndex : null;
        }
    }
}
