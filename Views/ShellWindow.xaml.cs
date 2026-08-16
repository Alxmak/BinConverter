using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        /// <summary>Сколько ждать после ухода мыши, прежде чем убрать всплывающий список.</summary>
        private static readonly TimeSpan PeekCloseDelay = TimeSpan.FromMilliseconds(400);

        private readonly DispatcherTimer _peekCloseTimer = new() { Interval = PeekCloseDelay };

        public ShellWindow()
        {
            InitializeComponent();
            DataContext = OperationLockService.Instance;

            _peekCloseTimer.Tick += (_, _) => ClosePeek();

            // Уход из окна мышь не отслеживает: переключились на другую программу — списку
            // висеть поверх неё незачем.
            Deactivated += (_, _) => ClosePeek();

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
            ClosePeek();
            RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
        }

        /// <summary>
        /// Наведение на кнопку показывает разделы всплывающим списком поверх страницы,
        /// ничего не двигая. Если панель и так развёрнута, показывать нечего.
        /// </summary>
        private void MenuToggle_MouseEnter(object sender, MouseEventArgs e)
        {
            if (RootNavigation.IsPaneOpen) return;

            // Отступ сверху делаем зрительно равным отступу слева: расстояние от верхнего
            // края окна до списка — такое же, как от левого края до него же.
            //
            // Оба меряем на месте: слева отступ получается из положения кнопки (она стоит
            // после значка и названия программы, а их ширина зависит от языка и масштаба),
            // а сверху Popup отсчитывается от нижнего края кнопки, поэтому высоту полосы
            // заголовка приходится вычитать. Без этого вычитания список уезжал вниз ровно
            // на высоту заголовка.
            Point button = MenuToggleButton.TranslatePoint(new Point(0, 0), this);
            double leftGap = button.X + MenuPeek.HorizontalOffset;
            MenuPeek.VerticalOffset = Math.Max(2, leftGap - (button.Y + MenuToggleButton.ActualHeight));

            BuildPeekMenu();

            _peekCloseTimer.Stop();
            MenuPeek.IsOpen = true;

            // Пока список открыт, подсказка кнопки не нужна: она говорит ровно то же,
            // что и сам список, и всплывала бы поверх него.
            SWC.ToolTipService.SetIsEnabled(MenuToggleButton, false);
        }

        private void MenuToggle_MouseLeave(object sender, MouseEventArgs e) => SchedulePeekClose();

        private void MenuPeek_MouseEnter(object sender, MouseEventArgs e) => _peekCloseTimer.Stop();

        private void MenuPeek_MouseLeave(object sender, MouseEventArgs e) => SchedulePeekClose();

        /// <summary>
        /// Закрытие откладывается, а не делается сразу: между кнопкой и списком есть зазор,
        /// и мышь по дороге к списку успевает уйти с обоих. Без задержки список закрывался бы
        /// прямо под курсором на полпути.
        /// </summary>
        private void SchedulePeekClose()
        {
            if (!MenuPeek.IsOpen) return;

            _peekCloseTimer.Stop();
            _peekCloseTimer.Start();
        }

        private void ClosePeek()
        {
            _peekCloseTimer.Stop();
            MenuPeek.IsOpen = false;
            SWC.ToolTipService.SetIsEnabled(MenuToggleButton, true);
        }

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

            // Вид пункта — в стиле PeekMenuItemStyle (ShellWindow.xaml): там же объяснено,
            // почему у него свой шаблон, а не библиотечный.
            var button = new SWC.Button
            {
                Content = row,
                Style = (Style)FindResource("PeekMenuItemStyle"),
                IsEnabled = item.IsEnabled
            };

            button.Click += (_, _) =>
            {
                ClosePeek();
                if (item.TargetPageType is not null) RootNavigation.Navigate(item.TargetPageType);
            };

            return button;
        }

        /// <summary>
        /// Подпись пункта меню. Сейчас у всех пунктов это строка, но так было не всегда:
        /// у «Извлечения разделов» подпись собиралась из двух Run — название и пометка
        /// «Beta». У такого TextBlock свойство Text пустое (текст лежит в Inlines),
        /// и раздел пропадал из всплывающего списка, оставляя один значок. Пометка
        /// переехала к заголовку группы, но проверка нестроки остаётся: подпись
        /// из чего-нибудь составного здесь дешевле пережить, чем потерять пункт.
        /// </summary>
        private static string TextOf(object? content) => content switch
        {
            string s => s,
            SWC.TextBlock block when !string.IsNullOrEmpty(block.Text) => block.Text,
            SWC.TextBlock block => string.Concat(block.Inlines.OfType<SWD.Run>().Select(run => run.Text)),
            _ => content?.ToString() ?? ""
        };
    }
}
