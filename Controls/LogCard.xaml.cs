using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Карточка журнала: заголовок, кнопки открыть/сохранить/очистить и сам список строк.
    /// Одна и та же карточка стоит в «Конвертировании», «Сборке файла» и «Проверке SHA-256» —
    /// раньше её разметка была скопирована в три страницы дословно, и любая правка отступов
    /// требовалась в трёх местах (а сдвиг строк журнала под заголовок карточки уже
    /// приходилось делать именно так).
    /// </summary>
    public partial class LogCard : UserControl
    {
        public LogCard()
        {
            InitializeComponent();

            // Подписываемся на Items, а не на саму коллекцию журнала: Items принадлежит
            // этому списку, поэтому отписываться не нужно — иначе три карточки (по одной
            // на раздел) держали бы статическую коллекцию LogService и текли при
            // переключении разделов.
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) => ScrollToLastEntry();

            // Журнал общий и к моменту открытия раздела уже может быть непустым —
            // показываем его конец, а не начало.
            Loaded += (_, _) => ScrollToLastEntry();

            // Выделение строки не должно оставаться навсегда: как только фокус уходит
            // из списка (клик по любому другому элементу окна) — снимаем его.
            LogList.IsKeyboardFocusWithinChanged += (_, e) =>
            {
                if (!(bool)e.NewValue) LogList.SelectedIndex = -1;
            };
        }

        /// <summary>
        /// Прокручивает журнал к последней записи. Строки добавляются из рабочего потока
        /// операции, но LogService переводит их в поток интерфейса через Dispatcher,
        /// поэтому уведомление приходит сюда уже там, где прокрутку делать можно.
        /// </summary>
        private void ScrollToLastEntry()
        {
            int last = LogList.Items.Count - 1;
            if (last < 0) return;

            LogList.ScrollIntoView(LogList.Items[last]);
        }
    }
}
