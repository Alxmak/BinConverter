using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TweakFirmware.Services;

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
        /// <summary>Допуск на округление при сравнении позиции прокрутки с её концом.</summary>
        private const double BottomTolerance = 1.0;

        /// <summary>
        /// «Липкий низ»: журнал догоняет последнюю запись, только пока пользователь и так
        /// смотрит на конец списка. Стоит отлистать вверх — прокрутка перестаёт вмешиваться,
        /// иначе каждая новая строка возвращала бы вниз прямо во время чтения.
        /// </summary>
        private bool _stickToBottom = true;

        public LogCard()
        {
            InitializeComponent();

            // Подписываемся на Items, а не на саму коллекцию журнала: Items принадлежит
            // этому списку, поэтому отписываться не нужно — иначе три карточки (по одной
            // на раздел) держали бы статическую коллекцию LogService и текли при
            // переключении разделов.
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged += OnLogChanged;

            // ScrollChanged всплывает от ScrollViewer'а внутри шаблона списка, поэтому
            // сам ScrollViewer искать в дереве не нужно.
            LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));

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

        private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Журнал очистили — начинаем заново, снова следуя за новыми строками.
            if (LogList.Items.Count == 0)
            {
                _stickToBottom = true;
                return;
            }

            if (_stickToBottom) ScrollToLastEntry();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Реагируем только на перемещение по списку. Рост содержимого — это добавление
            // строки, а не действие пользователя, и решение «липнуть или нет» менять
            // не должен: иначе первая же новая строка сама себя и разлипала бы.
            if (e.ExtentHeightChange != 0) return;

            _stickToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - BottomTolerance;
        }

        /// <summary>
        /// Ctrl+C — то же, что «Копировать» в контекстном меню. Список сам копирование
        /// не умеет, а выделение строки без возможности её забрать было бесполезным:
        /// из журнала обычно и нужен хэш или путь.
        /// </summary>
        private void LogList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            CopySelectedLines();
            e.Handled = true;
        }

        private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelectedLines();

        private void CopySelectedLines()
        {
            if (LogList.SelectedItems.Count == 0) return;

            // Порядок берём из самого списка, а не из порядка выделения: выделять можно
            // и снизу вверх, а скопированное должно читаться как в журнале.
            var text = new StringBuilder();
            foreach (var item in LogList.Items)
                if (LogList.SelectedItems.Contains(item))
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(item);
                }

            // Неудачу записи в буфер обмена разбирает ClipboardHelper — она возможна,
            // пока буфером владеет другое приложение, и мешать окном из-за этого не стоит.
            ClipboardHelper.TryCopy(text.ToString());
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
