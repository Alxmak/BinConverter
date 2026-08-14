using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TweakFirmware.Core;
using TweakFirmware.Services;
using TweakFirmware.ViewModels;

namespace TweakFirmware.Views
{
    public partial class ExtractPage : Page
    {
        private readonly ExtractViewModel _viewModel = TabViewModels.ExtractTab;

        /// <summary>Окно, пока страница открыта: с него снимается подписка при уходе.</summary>
        private Window? _window;

        public ExtractPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // ViewModel живёт дольше страницы: разобранный список разделов должен
            // пережить переход в «Настройки» и обратно.
            Loaded += (_, _) =>
            {
                _viewModel.Attach();

                // Окно одно на всю программу и переживает страницу, поэтому подписку
                // обязательно снимать: иначе каждый заход на вкладку добавлял бы ещё
                // один обработчик, а страница не собиралась бы сборщиком мусора.
                _window = Window.GetWindow(this);
                _window?.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown), true);
            };

            Unloaded += (_, _) =>
            {
                _viewModel.Detach();

                _window?.RemoveHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown));
                _window = null;
            };

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        /// <summary>
        /// Щелчок по строке: отмечает раздел, если попали в галочку, и выделяет строку.
        ///
        /// Выделение ставится своим кодом, а не отдаётся таблице, потому что штатное
        /// выделение приходит вместе с ненужным: с зажатой кнопкой DataGrid «закрашивает»
        /// строку за строкой по ходу движения мыши, а обратный ход снимает. Здесь строка
        /// выделяется ровно одна и ровно по щелчку.
        ///
        /// Щелчок гасится только внутри ячейки данных. Заголовки колонок и полосы
        /// прокрутки в ячейку не входят, поэтому их это не касается.
        /// </summary>
        private void Partitions_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsInsideCell(e)) return;

            // Отметку переключаем сами, а щелчок гасим целиком — включая щелчок
            // по самой галочке.
            //
            // Пропустить его нельзя, хотя галочка событие обрабатывает: DataGridCell
            // вешает свой обработчик выделения через RegisterClassHandler с
            // handledEventsToo, то есть он отрабатывает и по уже обработанному событию,
            // а «занято» проверяет только в одной из двух своих веток. Поэтому строка
            // выделялась именно при щелчке по галочке — и вместе с выделением
            // содержимое строки уезжало вправо на ширину полоски выделения.
            if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject)?.DataContext is PartitionRow row)
                row.Selected = !row.Selected;

            // Выделяем строку, по которой щёлкнули, — в том числе когда щёлкнули
            // по её галочке: это тот же щелчок по строке.
            PartitionsGrid.SelectedItem = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item;

            e.Handled = true;
        }

        /// <summary>
        /// Щелчок мимо таблицы снимает выделение — где бы в окне он ни случился.
        ///
        /// Обработчик висит на окне, а не на странице: «в другом месте программы» — это
        /// и меню разделов, и заголовок окна, а они лежат выше страницы. Ставится он
        /// с handledEventsToo, иначе щелчки по кнопкам и полям, которые обрабатывают
        /// их сами, проходили бы мимо и выделение оставалось бы висеть.
        /// </summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!ReferenceEquals(FindAncestor<DataGrid>(e.OriginalSource as DependencyObject), PartitionsGrid))
                PartitionsGrid.SelectedItem = null;
        }

        /// <summary>
        /// Правая кнопка выделяла строку и после того, как левую погасили, — потому что
        /// делает это не обработчик мыши, а <c>DataGrid.OnContextMenuOpening</c>: он
        /// намеренно выделяет ячейку под курсором, чтобы контекстное меню открывалось
        /// для неё. Меню у этой таблицы нет, а выделение оставалось.
        ///
        /// Гасится это отпусканием правой кнопки, а не нажатием: событие открытия меню
        /// поднимает <c>PopupControlService</c> уже на отпускании и только если оно
        /// не обработано. Отключить меню через <c>ContextMenuService.IsEnabled</c> нельзя —
        /// эта проверка идёт позже, когда строка уже выделена.
        ///
        /// Галочка здесь без исключения: правой кнопкой она ничего не делает, а выделять
        /// строку щелчком по ней тем более незачем.
        /// </summary>
        private void Partitions_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsInsideCell(e)) e.Handled = true;
        }

        private static bool IsInsideCell(RoutedEventArgs e) =>
            FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) != null;

        private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

        // Перетаскивание принимают только поля путей — см. FileDropHelper. События
        // перетаскивания доходят и до отключённых элементов, поэтому во время работы
        // отсекаем их здесь явно.
        private void SourceBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        private void SourceBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] files) return;

            _viewModel.SourcePath = files[0];
        }

        private void OutputBox_DragOver(object sender, DragEventArgs e) =>
            FileDropHelper.SetEffect(e, !_viewModel.IsBusy);

        private void OutputBox_Drop(object sender, DragEventArgs e)
        {
            if (FileDropHelper.TakePaths(e, !_viewModel.IsBusy) is not string[] paths) return;

            string? folder = DroppedFolder.Resolve(paths[0]);
            if (folder != null) _viewModel.OutputPath = folder;
        }
    }
}
