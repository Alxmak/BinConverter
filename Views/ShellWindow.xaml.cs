using System;
using System.Windows;
using TweakFirmware.Services;
using Wpf.Ui.Controls;

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

            // Выбрали раздел из подсмотренного меню — меню снова прячется. Закреплённое
            // (открытое щелчком) остаётся на месте: человек попросил его показать явно.
            RootNavigation.Navigated += (_, _) =>
            {
                if (!_menuPinned) RootNavigation.IsPaneVisible = false;
            };
        }

        /// <summary>
        /// Меню открыто щелчком, а не наведением. Закреплённое не прячется само:
        /// ни при уходе мыши, ни после выбора раздела.
        /// </summary>
        private bool _menuPinned = true;

        /// <summary>
        /// Насколько правее панели должна уйти мышь, чтобы подсмотренное меню закрылось.
        /// Без запаса меню моргало бы на границе: курсор дрожит на пиксель, и панель
        /// то прячется, то появляется снова.
        /// </summary>
        private const double PeekCloseMargin = 60;

        /// <summary>Щелчок по кнопке: показать меню и закрепить — или спрятать совсем.</summary>
        private void MenuToggle_Click(object sender, RoutedEventArgs e)
        {
            _menuPinned = !_menuPinned;
            RootNavigation.IsPaneVisible = _menuPinned;
        }

        /// <summary>
        /// Наведение на кнопку показывает меню, не закрепляя его: это «подсмотреть»,
        /// а не «открыть». Уберёт его либо выбор раздела, либо уход мыши вправо
        /// (см. Root_PreviewMouseMove).
        /// </summary>
        private void MenuToggle_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_menuPinned) RootNavigation.IsPaneVisible = true;
        }

        /// <summary>
        /// Подсмотренное меню прячется, когда мышь уходит от него в сторону содержимого.
        /// Слушаем окно целиком, а не панель: панель — часть шаблона NavigationView,
        /// добраться до неё из разметки нечем, а положение курсора известно и так.
        /// </summary>
        private void Root_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_menuPinned || !RootNavigation.IsPaneVisible) return;

            if (e.GetPosition(this).X > RootNavigation.OpenPaneLength + PeekCloseMargin)
                RootNavigation.IsPaneVisible = false;
        }
    }
}
