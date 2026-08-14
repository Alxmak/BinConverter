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

        public ExtractPage()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // ViewModel живёт дольше страницы: разобранный список разделов должен
            // пережить переход в «Настройки» и обратно.
            Loaded += (_, _) => _viewModel.Attach();
            Unloaded += (_, _) => _viewModel.Detach();

            PageScrollHelper.AttachWheelScrolling(this, RootScroll);
        }

        /// <summary>
        /// Гасит выделение строк таблицы разделов.
        ///
        /// Выделение здесь ни на что не влияет — разделы отмечаются галочками, — зато
        /// мешало дважды. В шаблоне строки WPF-UI выделенная строка получает слева полоску
        /// шириной 3px, и стоит она в отдельной колонке сетки шириной Auto: пока полоски
        /// нет, колонка нулевая, а как только строка выделилась — всё содержимое, вместе
        /// с галочкой, съезжает вправо. Второе: с зажатой кнопкой мыши DataGrid «закрашивает»
        /// строки выделением по ходу движения, чего на этой вкладке никто не просил.
        ///
        /// Щелчок гасится только внутри ячейки данных и только если он не по галочке.
        /// Заголовки колонок (сортировка) и полосы прокрутки в ячейку не входят, поэтому
        /// их это не касается.
        /// </summary>
        private void Partitions_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Галочку пропускаем: щелчок по ней ставит отметку, а не выделяет строку.
            if (IsInsideCell(e) && FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) == null)
                e.Handled = true;
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
