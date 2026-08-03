using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Колесо мыши над журналом должно скроллить только сам список, никогда не
    /// "перетекая" на скролл всей страницы — даже когда список уже упёрся в свой
    /// предел. Без этого, доскроллив журнал до конца, пользователь тем же движением
    /// колеса неожиданно начинал скроллить страницу (стандартное поведение WPF для
    /// вложенных ScrollViewer'ов, которое здесь не нужно).
    /// </summary>
    public static class LogScrollHelper
    {
        public static bool TryHandleListBoxWheel(MouseWheelEventArgs e)
        {
            var listBox = FindAncestor<ListBox>(e.OriginalSource as DependencyObject);
            if (listBox == null) return false;

            var scrollViewer = FindDescendant<ScrollViewer>(listBox);
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
