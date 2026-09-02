using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    /// <summary>
    /// Общая часть всех рабочих вкладок: занятость, отмена, пауза, оценка «сколько
    /// осталось» и связь с <see cref="OperationLockService"/>.
    ///
    /// Всё это лежало в четырёх вкладках четырьмя копиями — вплоть до дословно
    /// совпадающих текстов подтверждения отмены и одинаковых блоков finally. Копии
    /// успели разойтись: «Извлечение разделов» по окончании работы возвращало подпись
    /// кнопки паузы в исходное состояние, а «Конвертирование» и «Сборка» — нет, и после
    /// отменённой на паузе операции кнопка оставалась «Возобновить» до следующего пуска.
    ///
    /// Работа вкладки размечена тремя точками:
    /// <see cref="PrepareOperation"/> — до запуска: токен отмены и чистая оценка;
    /// <see cref="MarkStarted"/> — работа действительно началась (у трёх вкладок из
    /// четырёх об этом сообщает сама операция: до первого байта успевают пройти проверки
    /// места и вопрос о перезаписи, а ждать ответа человека — не работа, и в скорость
    /// это попадать не должно);
    /// <see cref="FinishOperation"/> — в finally, чем бы дело ни кончилось.
    /// </summary>
    public abstract partial class OperationTabViewModel : LogHostViewModel
    {
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        private bool isBusy;

        /// <summary>Пока идёт работа, поля и параметры вкладки недоступны — менять их
        /// на ходу нельзя, иначе настройки разойдутся с уже запущенной операцией.</summary>
        public bool IsNotBusy => !IsBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PauseButtonText))]
        private bool isPaused;

        /// <summary>
        /// Подпись кнопки паузы. Вычисляется, а не присваивается: раньше её ставили
        /// в восьми местах трёх вкладок, и одно из них забыли — в finally, откуда
        /// операция уходит с уже снятой паузой. Кнопка гасла с надписью «Возобновить»
        /// и держала её до следующего запуска.
        /// </summary>
        public string PauseButtonText => Strings.Get(IsPaused ? "Common_ResumeButton" : "Common_PauseButton");

        /// <summary>
        /// Поддерживает ли пауза смысл у той работы, что идёт сейчас. Разбор дампа читает
        /// файл короткими прыжками по адресам — приостанавливать там нечего; сверка хэшей
        /// только читает; а нарезка, сборка и извлечение пишут гигабайты подряд.
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
        private bool supportsPause;

        public virtual bool CanPause => IsBusy && SupportsPause;

        /// <summary>
        /// Контроллер паузы один на вкладку и живёт дольше любой операции. Поэтому
        /// <see cref="FinishOperation"/> обязан снимать паузу: оставленная остановила бы
        /// следующую работу ещё до первого байта — без нажатой кнопки и без объяснения.
        /// </summary>
        protected PauseController Pause { get; } = new();

        /// <summary>Отмена текущей работы. Вне операции — <c>null</c>.</summary>
        protected CancellationTokenSource? Cts { get; private set; }

        /// <summary>
        /// Оценка «сколько осталось» под полосой прогресса. Часы отдельные, а не
        /// DateTime.Now в каждом отсчёте: разность двух моментов Stopwatch не зависит
        /// от того, перевели ли за это время системные часы.
        /// </summary>
        private readonly SpeedEstimator _speed = new();
        private readonly Stopwatch _clock = new();

        /// <summary>
        /// Считает ли текущая работа байты. Если нет, скорость показывать нельзя:
        /// у проходов разбора счёт идёт то в страницах, то в разделах, и «180 МБ/с»
        /// на них было бы просто неправдой. Оценка времени от единиц не зависит:
        /// доля есть доля.
        /// </summary>
        private bool _countsBytes;

        /// <summary>
        /// Начало подписи под полосой — то, что показывается и без оценки: имя текущего
        /// файла с номером или название прохода.
        /// </summary>
        protected string CaptionBase { get; set; } = "";

        /// <summary>
        /// Куда вкладка кладёт готовую подпись. У каждой она называется по-своему
        /// (<c>CurrentFileLabel</c>, <c>StageLabel</c>) и привязана к своей полосе.
        /// </summary>
        protected abstract void ApplyCaption(string text);

        /// <summary>
        /// Подпись кнопки паузы собрана из словаря, а вычисляемое свойство само о смене
        /// языка не узнает — просим разметку перечитать его. Вкладки, дополняющие этот
        /// метод своим, обязаны позвать базовый: иначе кнопка останется на прежнем языке.
        /// </summary>
        protected override void OnLanguageChanged() => OnPropertyChanged(nameof(PauseButtonText));

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanPause));
            OnBusyChanged(value);
        }

        partial void OnSupportsPauseChanged(bool value) => OnPropertyChanged(nameof(CanPause));

        /// <summary>
        /// Занятость изменилась. Здесь вкладка будит свои команды: у каждой они свои
        /// («Начать», «Извлечь отмеченные», «Добавить файл»), а RelayCommand из
        /// CommunityToolkit сам ничего не перепроверяет.
        /// </summary>
        protected virtual void OnBusyChanged(bool busy) { }

        partial void OnIsPausedChanged(bool value)
        {
            OperationLockService.Instance.IsPaused = value;

            // На паузе оценка врёт: время идёт, а работа — нет. Убираем её из подписи,
            // а после возобновления она посчитается заново, с чистого листа: иначе первые
            // секунды показывали бы скорость, размазанную по простою.
            if (!value) return;

            _speed.Reset();
            ApplyCaption(CaptionBase);
        }

        /// <summary>
        /// Отдать общий ход работы полосе на кнопке в панели задач — это единственное,
        /// что видно у свёрнутого окна, а операции здесь идут десятками минут.
        /// </summary>
        protected static void ReportTaskbarProgress(double percent) =>
            OperationLockService.Instance.Progress = percent / 100.0;

        /// <summary>Забыть накопленную оценку: работа началась заново или сменился проход.</summary>
        protected void ResetEstimate()
        {
            _speed.Reset();
            _clock.Restart();
        }

        /// <summary>
        /// Очередной отсчёт: пересобирает подпись под полосой из <see cref="CaptionBase"/>,
        /// оценки оставшегося и скорости — последние две добавляются, только если уже
        /// есть из чего их посчитать.
        /// </summary>
        protected void UpdateCaption(long doneBytes, long totalBytes)
        {
            _speed.Add(_clock.Elapsed, doneBytes);

            ApplyCaption(ProgressCaption.Build(
                CaptionBase,
                _countsBytes ? _speed.BytesPerSecond : null,
                _speed.Remaining(totalBytes)));
        }

        /// <summary>Токен отмены для будущей работы. Зовётся до её запуска.</summary>
        protected CancellationToken PrepareOperation()
        {
            Cts?.Dispose();
            Cts = new CancellationTokenSource();

            ResetEstimate();
            return Cts.Token;
        }

        /// <summary>Все проверки пройдены, работа началась.</summary>
        protected void MarkStarted(bool supportsPause = true, bool countsBytes = true)
        {
            IsBusy = true;
            IsPaused = false;
            SupportsPause = supportsPause;
            _countsBytes = countsBytes;

            OperationLockService.Instance.OperationStarted(CancelNow);

            // Часы пускаются здесь, а не в PrepareOperation: между нажатием кнопки
            // и первым байтом успевают пройти проверки и вопросы, а это не работа.
            ResetEstimate();
        }

        /// <summary>
        /// Конец работы — успехом, ошибкой или отменой. Вкладки дополняют этот метод
        /// своими полосами прогресса, но занятость, паузу и блокировку снимает он один:
        /// пока это делали четыре копии, они и разошлись.
        /// </summary>
        protected virtual void FinishOperation()
        {
            Pause.Resume();
            IsPaused = false;
            SupportsPause = false;
            IsBusy = false;

            // Подпись под полосой пустая — строка сама скрывается (см. ProgressRow):
            // оставленное «файл 3 из 3 · 180 МБ/с» под обнулённой полосой говорило бы
            // о работе, которой уже нет.
            CaptionBase = "";
            ApplyCaption("");

            OperationLockService.Instance.OperationFinished();

            // Dispose до обнуления: и здесь, и в CancelNow мы в потоке интерфейса,
            // поэтому «отменить уже освобождённый» невозможно.
            Cts?.Dispose();
            Cts = null;
        }

        /// <summary>
        /// Чем пугать при отмене. По умолчанию — записью: её прерывание удаляет
        /// недописанные файлы. У сверки хэшей своё сообщение: она только читает,
        /// и терять там нечего.
        /// </summary>
        protected virtual string CancelConfirmMessageKey => "Common_CancelConfirmWritingMessage";

        /// <summary>
        /// Спрашивает, прежде чем прервать. Не спрашивала ни кнопка, ни Esc, а цена
        /// ошибки здесь — вся работа: на дампе в несколько гигабайт это десятки минут
        /// заново. Esc вдобавок рефлекторная клавиша «закрыть что-нибудь ненужное»,
        /// а «Отмена» — крупная красная кнопка в восьми пикселях под «Начать».
        /// </summary>
        [RelayCommand(CanExecute = nameof(IsBusy))]
        private async Task CancelAsync()
        {
            var answer = await DialogService.ShowConfirmAsync(
                Strings.Get("Common_CancelConfirmTitle"),
                Strings.Get(CancelConfirmMessageKey),
                Strings.Get("Common_CancelConfirmStop"),
                null,
                Strings.Get("Common_CancelConfirmKeep"));

            if (answer == DialogChoice.Primary) CancelNow();
        }

        /// <summary>
        /// Прервать молча. Тем же способом операцию останавливает закрытие окна — там
        /// решение уже принято в своём диалоге, и спрашивать второй раз незачем.
        /// Пауза снимается до отмены: иначе работа так и стояла бы на паузе, не дойдя
        /// до проверки токена.
        /// </summary>
        protected void CancelNow()
        {
            Pause.Resume();
            Cts?.Cancel();
        }

        [RelayCommand(CanExecute = nameof(CanPause))]
        private void TogglePause()
        {
            if (IsPaused) Pause.Resume();
            else Pause.Pause();

            IsPaused = Pause.IsPaused;

            // В журнал пишут все вкладки одинаково — «Извлечение разделов» до сих пор
            // не писало вовсе, и по журналу выходило, что операция шла без остановок.
            AppLogger.Log(Strings.Get(IsPaused ? "Common_PausedLog" : "Common_ResumedLog"));
        }
    }
}
