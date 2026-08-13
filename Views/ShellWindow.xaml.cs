using System;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TweakFirmware.Services;
using Wpf.Ui.Controls;

// Имена контролов WPF и WPF-UI пересекаются (TextBlock, Button и другие), поэтому
// родные берём через псевдонимы, а не общим using — про эти грабли сказано в CLAUDE.md.
using SWC = System.Windows.Controls;
using SWD = System.Windows.Documents;

namespace TweakFirmware.Views
{
    public partial class ShellWindow : FluentWindow
    {
        public ShellWindow()
        {
            InitializeComponent();
            DataContext = OperationLockService.Instance;

            // Mica — эффект DWM из Windows 11; на более старых системах (в т.ч. Windows 10)
            // используем Acrylic. Выбор подложки живёт в ThemeService, чтобы он не разъезжался
            // с тем, что применяется при переключении темы. Скруглённые углы окна — тоже
            // возможность DWM из Windows 11, на Windows 10 не имеет смысла и не включаем её.
            bool isWindows11OrNewer = Environment.OSVersion.Version.Build >= 22000;
            WindowBackdropType = ThemeService.PreferredBackdrop;
            WindowCornerPreference = isWindows11OrNewer ? WindowCornerPreference.Round : WindowCornerPreference.Default;

            // Тёмный режим самого окна (заголовок, рамка) на старте не применяется: тему
            // выставляем до создания окна, а WPF-UI делает эту часть только для уже
            // существующего MainWindow. Здесь дескриптор окна уже есть — доводим сами,
            // иначе на тёмной теме окно остаётся светлым, а текст на нём — почти белым.
            SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);

            WindowPlacementService.Restore(this);
            Loaded += (_, _) => RootNavigation.Navigate(typeof(ConvertPage));
            Closing += (_, _) => WindowPlacementService.Save(this);
        }

        /// <summary>
        /// Щелчок по кнопке разворачивает или прячет саму панель. Панель встроена
        /// в раскладку, поэтому развёрнутая раздвигает страницу — так и задумано:
        /// щелчок означает «оставить меню открытым», а не «подсмотреть».
        /// </summary>
        private void MenuToggle_Click(object sender, RoutedEventArgs e)
        {
            MenuPeek.IsOpen = false;
            RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
        }

        /// <summary>
        /// Наведение на кнопку показывает разделы всплывающим списком поверх страницы,
        /// ничего не двигая. Если панель и так развёрнута, показывать нечего.
        /// </summary>
        private void MenuToggle_MouseEnter(object sender, MouseEventArgs e)
        {
            if (RootNavigation.IsPaneOpen) return;

            BuildPeekMenu();
            MenuPeek.IsOpen = true;
        }

        private void MenuPeek_MouseLeave(object sender, MouseEventArgs e) => MenuPeek.IsOpen = false;

        /// <summary>
        /// Собирает всплывающий список из тех же пунктов, что стоят в панели.
        /// Не копия разметки, а пересборка по RootNavigation.MenuItems: второй список
        /// разделов в XAML разошёлся бы с первым на первом же новом разделе.
        ///
        /// Пересобирается на каждое открытие, а не один раз: так подхватываются и смена
        /// языка (подписи привязаны к словарю), и запрет на переключение во время
        /// операции (IsEnabled у пунктов меняется на ходу).
        /// </summary>
        private void BuildPeekMenu()
        {
            MenuPeekItems.Children.Clear();

            var lineBrush = TryFindResource("CardStrokeColorDefaultBrush") as Brush;

            // Запасной цвет — цвет текста самого окна: у значка WPF-UI кисть объявлена
            // ненулевой, и передать туда «не нашлось» нельзя.
            Brush captionBrush = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Foreground;

            foreach (object entry in RootNavigation.MenuItems)
            {
                switch (entry)
                {
                    case NavigationViewItemHeader header:
                        MenuPeekItems.Children.Add(new SWC.TextBlock
                        {
                            Text = header.Text,
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            Opacity = 0.7,
                            Foreground = captionBrush,
                            Margin = new Thickness(10, 10, 10, 4)
                        });
                        break;

                    case NavigationViewItemSeparator:
                        MenuPeekItems.Children.Add(new SWC.Border
                        {
                            Height = 1,
                            Background = lineBrush,
                            Margin = new Thickness(8, 6, 8, 6)
                        });
                        break;

                    case NavigationViewItem item:
                        MenuPeekItems.Children.Add(CreatePeekItem(item, captionBrush));
                        break;
                }
            }
        }

        private SWC.Button CreatePeekItem(NavigationViewItem item, Brush foreground)
        {
            var row = new SWC.StackPanel { Orientation = SWC.Orientation.Horizontal };

            // Значок пересоздаём, а не переиспользуем: у элемента WPF может быть только
            // один родитель, и перенос значка из панели во всплывающий список выдернул бы
            // его из самой панели.
            if (item.Icon is SymbolIcon icon)
            {
                row.Children.Add(new SymbolIcon
                {
                    Symbol = icon.Symbol,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 10, 0),
                    Foreground = foreground
                });
            }

            row.Children.Add(new SWC.TextBlock { Text = TextOf(item.Content), Foreground = foreground });

            var button = new SWC.Button
            {
                Content = row,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                IsEnabled = item.IsEnabled,
                FontFamily = new FontFamily("Cambria, Georgia, Times New Roman")
            };

            button.Click += (_, _) =>
            {
                MenuPeek.IsOpen = false;
                if (item.TargetPageType is not null) RootNavigation.Navigate(item.TargetPageType);
            };

            return button;
        }

        /// <summary>
        /// Подпись пункта меню. У большинства пунктов это строка, а у «Извлечения
        /// разделов» — TextBlock, собранный из двух Run: название и надстрочная пометка
        /// «Beta». У такого TextBlock свойство Text пустое (текст лежит в Inlines),
        /// поэтому раздел и пропадал из всплывающего списка — оставался один значок.
        /// Собираем подпись из Run'ов; пометка приходит вместе с названием обычным
        /// текстом, повторять её оформление во всплывающем списке незачем.
        /// </summary>
        private static string TextOf(object? content)
        {
            if (content is string s) return s;

            if (content is SWC.TextBlock block)
            {
                if (!string.IsNullOrEmpty(block.Text)) return block.Text;

                var parts = new StringBuilder();
                foreach (SWD.Inline inline in block.Inlines)
                    if (inline is SWD.Run run) parts.Append(run.Text);

                return parts.ToString();
            }

            return content?.ToString() ?? "";
        }
    }
}
