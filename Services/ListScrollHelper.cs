using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Колесо мыши над списком со своей прокруткой должно двигать сам список, а не
    /// страницу под ним, и никогда не «перетекать» на страницу — даже когда список уже
    /// упёрся в свой предел. Без этого, доскроллив список до конца, пользователь тем же
    /// движением колеса неожиданно начинал скроллить страницу (стандартное поведение WPF
    /// для вложенных ScrollViewer'ов, которое здесь не нужно).
    ///
    /// Таких списков в программе два: журнал внизу каждой вкладки и таблица найденных
    /// разделов в «Извлечении разделов». Типы перечислены явно, а не взяты как любой
    /// ItemsControl: под это определение попадает и выпадающий список пресетов, где
    /// колесо должно работать по-своему.
    /// </summary>
    public static class ListScrollHelper
    {
        /// <summary>
        /// Прокручивает список под курсором и сообщает, что колесо обработано.
        /// Возвращает false, если курсор не над списком, — тогда страницу прокручивает
        /// вызывающий (<see cref="PageScrollHelper"/>).
        /// </summary>
        public static bool TryHandleWheel(MouseWheelEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;

            // Сначала ищем ближайший из двух списков вверх по дереву: у таблицы разделов
            // внутри есть и свои ячейки, и флажки, и событие приходит от них.
            DependencyObject? list = FindAncestor<ListBox>(source) ?? (DependencyObject?)FindAncestor<DataGrid>(source);
            if (list == null) return false;

            var scrollViewer = FindDescendant<ScrollViewer>(list);
            scrollViewer?.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);

            e.Handled = true;
            return true;
        }

        private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;

                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
