using System;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TweakFirmware.Core;
using TweakFirmware.Services;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Карточка журнала: заголовок, кнопки открыть/сохранить/очистить, список строк и
    /// ручка высоты под ним. Одна и та же карточка стоит на всех рабочих вкладках —
    /// раньше её разметка была скопирована в страницы дословно, и любая правка отступов
    /// требовалась в нескольких местах (а сдвиг строк журнала под заголовок карточки уже
    /// приходилось делать именно так).
    /// </summary>
    public partial class LogCard : UserControl
    {
        /// <summary>Допуск на округление при сравнении позиции прокрутки с её концом.</summary>
        private const double BottomTolerance = 1.0;

        /// <summary>Сколько строк журнала видно сразу после запуска программы.</summary>
        private const int DefaultVisibleLines = 5;

        private const int MinVisibleLines = 1;
        private const int MaxVisibleLines = 10;

        /// <summary>
        /// Запасная высота строки на случай, если измерить настоящую не вышло: журнал
        /// должен открыться хоть какой-то высоты, а не полоской в ноль пикселей.
        /// </summary>
        private const double FallbackRowHeight = 36;

        /// <summary>
        /// Высота журнала общая на все вкладки: карточка на каждой странице своя, и без
        /// общего значения растянутый журнал схлопывался бы обратно при переходе в соседний
        /// раздел. Применяется в конструкторе и в Loaded — первое нужно виртуализации
        /// (см. ниже), второе тому, что страницы кэшируются и созданная раньше карточка
        /// про новое значение сама не узнает. В настройки не пишется намеренно —
        /// при следующем запуске программы снова пять строк.
        /// </summary>
        private static int _visibleLines = DefaultVisibleLines;

        /// <summary>Высота одной строки списка. Меряется один раз на карточку.</summary>
        private double _rowHeight;

        /// <summary>Счёт высоты на время перетаскивания ручки. Вне перетаскивания — null.</summary>
        private LogHeightDrag? _drag;

        /// <summary>
        /// Окно и положение мыши в нём на момент захвата ручки. Мышь отслеживается
        /// относительно окна, потому что окно за время перетаскивания не двигается,
        /// а всё, что внутри карточки, — двигается: см. ResizeGrip_DragDelta.
        /// </summary>
        private Window? _dragWindow;
        private double _dragStartMouseY;

        /// <summary>
        /// «Липкий низ»: журнал догоняет последнюю запись, только пока пользователь и так
        /// смотрит на конец списка. Стоит отлистать вверх — прокрутка перестаёт вмешиваться,
        /// иначе каждая новая строка возвращала бы вниз прямо во время чтения.
        /// </summary>
        private bool _stickToBottom = true;

        /// <summary>Прокрутка уже поставлена в очередь — второй раз ставить не нужно.</summary>
        private bool _scrollQueued;

        /// <summary>ScrollViewer из шаблона списка. Появляется с первым ScrollChanged.</summary>
        private ScrollViewer? _scroller;

        public LogCard()
        {
            InitializeComponent();

            // Высота ставится здесь, до первого прохода разметки, и это не косметика.
            //
            // Список показывает строки виртуализированно — держит живыми только видимые, —
            // но лишь пока ему есть во что вписываться. Своей высоты у него нет, а карточка
            // стоит в StackPanel внутри страницы-скроллера, и оба меряют содержимое
            // в бесконечной высоте. В бесконечность «видно всё»: WPF создаёт и меряет
            // разом все строки журнала. Журнал общий на программу и держит до 5000 строк,
            // так что после разбора дампа каждый переход на раздел с журналом упирался
            // в создание тысяч элементов — переключение подвисало тем сильнее, чем больше
            // накопилось записей. На разделах без журнала («Настройки», «О программе»)
            // задержки не было.
            //
            // Loaded для этого не годится: он приходит уже после первого прохода разметки
            // (Render в очереди диспетчера идёт раньше Loaded), то есть все строки к тому
            // времени созданы. Та же ловушка обойдена в таблице разделов, но случайно:
            // у неё MaxHeight задан прямо в разметке.
            //
            // Ниже высота ставится ещё и в Loaded, и это не отменяет сказанного:
            // там она уже не «первая», а догоняет общее на все вкладки число строк.
            ApplyVisibleLines();

            // Подписываемся на Items, а не на саму коллекцию журнала: Items принадлежит
            // этому списку, поэтому отписываться не нужно — иначе три карточки (по одной
            // на раздел) держали бы статическую коллекцию LogService и текли при
            // переключении разделов.
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged += OnLogChanged;

            // ScrollChanged всплывает от ScrollViewer'а внутри шаблона списка, поэтому
            // сам ScrollViewer искать в дереве не нужно.
            LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));

            // Журнал общий и к моменту открытия раздела уже может быть непустым —
            // показываем его конец, а не начало. Прокрутка остаётся в Loaded: ей нужен
            // ScrollViewer из шаблона списка, а он появляется только с применением шаблона.
            Loaded += (_, _) =>
            {
                // Высота ставится и здесь, вторым разом. Страницы теперь кэшируются
                // (NavigationCacheMode="Required" в ShellWindow.xaml), то есть карточка
                // соседнего раздела была создана когда-то раньше и о новом числе строк
                // не знает: пока страницы создавались заново, за неё это делал сам
                // конструктор. Без этого растянутый журнал схлопывался бы обратно
                // при переходе в соседний раздел.
                ApplyVisibleLines();
                ScrollToLastEntry();
            };

            // Выделение строки не должно оставаться навсегда: как только фокус уходит
            // из списка (клик по любому другому элементу окна) — снимаем его.
            LogList.IsKeyboardFocusWithinChanged += (_, e) =>
            {
                if (!(bool)e.NewValue) LogList.SelectedIndex = -1;
            };
        }

        /// <summary>Ставит журналу высоту, кратную строке, по общему для всех вкладок числу строк.</summary>
        private void ApplyVisibleLines()
        {
            if (_rowHeight <= 0) _rowHeight = MeasureRowHeight();

            LogList.Height = Math.Round(_rowHeight * _visibleLines);
        }

        /// <summary>
        /// Высота строки меряется настоящим элементом списка, а не считается из размера
        /// шрифта: к тексту добавляются отступы ListBoxItem из темы WPF-UI (те самые 12px,
        /// из-за которых строки журнала пришлось двигать под заголовок карточки). Число,
        /// подобранное на глаз, разъехалось бы с ними при первом же обновлении библиотеки,
        /// и «пять строк» перестало бы быть пятью.
        /// </summary>
        private double MeasureRowHeight()
        {
            var probe = new ListBoxItem
            {
                Content = "0",
                FontFamily = LogList.FontFamily,
                FontSize = LogList.FontSize
            };

            // Стиль ставим только если он нашёлся: без него мерка даст высоту строки
            // со стандартными отступами — не точно, но и не ноль.
            if (TryFindResource("LogLine") is Style rowStyle) probe.Style = rowStyle;

            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            return probe.DesiredSize.Height > 0 ? probe.DesiredSize.Height : FallbackRowHeight;
        }

        private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (_rowHeight <= 0) _rowHeight = MeasureRowHeight();

            _dragWindow = Window.GetWindow(this);
            if (_dragWindow == null) return;

            _dragStartMouseY = Mouse.GetPosition(_dragWindow).Y;

            // Отсчитываем от кратной строке высоты, а не от ActualHeight: разойтись
            // они могут только на округление, но так точка отсчёта одна и та же
            // независимо от того, успела ли разметка отработать прошлое изменение.
            _drag = new LogHeightDrag(_visibleLines * _rowHeight, _rowHeight, MinVisibleLines, MaxVisibleLines);
        }

        /// <summary>
        /// Высота всегда кратна строке: наполовину показанная строка внизу читается
        /// как обрезанная, а тянуть журнал точнее, чем по строке, незачем.
        /// </summary>
        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_drag == null || _dragWindow == null) return;

            // Смещение берём положением мыши в окне, а не полем e.VerticalChange.
            //
            // Thumb присылает в нём сдвиг от точки захвата, посчитанный в собственных
            // координатах ручки, — то есть значение накопительное, а не приращение
            // с прошлого события. Складывать такие смещения нельзя: за десяток
            // событий из ровного хода мыши в 10 пикселей набегает под сотню.
            //
            // Хуже того, ручка стоит под журналом и уезжает вниз вместе с ним, так что
            // на каждое изменение высоты это поле прыгает на целую строку назад. Вместе
            // это и давало дрожание: журнал разворачивался на строку, следующее смещение
            // приходило отрицательным и сворачивал обратно — и так по кругу.
            int lines = _drag.LinesFor(Mouse.GetPosition(_dragWindow).Y - _dragStartMouseY);
            if (lines == _visibleLines) return;

            _visibleLines = lines;
            ApplyVisibleLines();

            // Тянут за нижний край: без этого уменьшенный журнал остался бы показывать
            // середину списка, хотя человек смотрел на последнюю запись.
            if (_stickToBottom) ScrollToLastEntry();
        }

        private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _drag = null;
            _dragWindow = null;
        }

        private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Журнал очистили — начинаем заново, снова следуя за новыми строками.
            if (LogList.Items.Count == 0)
            {
                _stickToBottom = true;
                return;
            }

            if (_stickToBottom) ScheduleScrollToLastEntry();
        }

        /// <summary>
        /// Прокрутка откладывается до следующего свободного прохода диспетчера, а не
        /// делается сразу.
        ///
        /// Причина: ScrollIntoView запускает пересчёт разметки, а вызывается это изнутри
        /// уведомления об изменении коллекции — то есть в момент, когда ItemsControl ещё
        /// не согласовал своё состояние с источником. Если во время этого пересчёта
        /// придёт следующее изменение (а при записи журнала строки идут очередями), список
        /// падает с InvalidOperationException «не соответствует своему источнику элементов».
        /// Ровно это и случилось: одно такое исключение раскрутилось в бесконечный круг
        /// и подвесило окно.
        ///
        /// Флаг ещё и склеивает очередь: на десяток строк подряд — одна прокрутка,
        /// а не десять.
        /// </summary>
        private void ScheduleScrollToLastEntry()
        {
            if (_scrollQueued) return;
            _scrollQueued = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _scrollQueued = false;

                // Страницу могли закрыть, пока прокрутка ждала своей очереди. Прокручивать
                // список, которого нет на экране, не нужно и небезопасно.
                if (IsLoaded) ScrollToLastEntry();
            }));
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Событие поднимает сам ScrollViewer, так что искать его в дереве не нужно —
            // запоминаем при первом же движении. Нужен он прокрутке в конец.
            _scroller ??= e.OriginalSource as ScrollViewer;

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

            // Двигаем сам ScrollViewer, а не просим список показать последний элемент.
            //
            // ScrollIntoView принимает не номер, а сам элемент, и ищет его перебором
            // с проверкой на равенство. Элементы здесь — строки, то есть равенство
            // по содержимому: две одинаковые записи подряд (а в журнале это обычное
            // дело — отметка времени идёт с точностью до секунды) уводили бы прокрутку
            // к первой из них, и журнал переставал бы следовать за концом. Плюс перебор
            // идёт по всем строкам, а вызывается это на каждую пачку записей.
            if (_scroller is not null) _scroller.ScrollToBottom();
            else LogList.ScrollIntoView(LogList.Items[last]);
        }
    }
}
