using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Прокрутка страницы колесом мыши. Раньше этот обработчик был скопирован в пять
    /// код-бихайндов — по три строки на страницу плюс подписка с одним и тем же
    /// пояснением, — и любая правка требовалась в пяти местах.
    /// </summary>
    public static class PageScrollHelper
    {
        /// <summary>
        /// Подписывает страницу на колесо мыши так, чтобы прокрутка работала над всей
        /// её площадью, а не только над самим скроллбаром.
        /// </summary>
        /// <param name="page">Страница: на неё ставится обработчик.</param>
        /// <param name="scroll">Корневой ScrollViewer этой страницы.</param>
        public static void AttachWheelScrolling(UIElement page, ScrollViewer scroll)
        {
            // handledEventsToo=true — иначе колесо мыши работает только тогда, когда
            // родительский NavigationView/Frame ещё не пометил событие обработанным
            // (на практике это давало прокрутку только над самим скроллбаром).
            page.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
            {
                // Списки со своей прокруткой (журнал, таблица разделов) двигаются сами,
                // изолированно от страницы — см. ListScrollHelper. Если курсор не над
                // таким списком, проверка возвращает false, и страница листается как обычно.
                if (ListScrollHelper.TryHandleWheel(e)) return;

                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
                e.Handled = true;
            }), true);
        }
    }
}
