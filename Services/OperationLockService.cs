using System;
using System.Threading.Tasks;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Единая на всё приложение точка правды «идёт ли сейчас операция».
    ///
    /// Две роли. Первая, давняя: пока операция идёт, оболочка не даёт переключать разделы,
    /// а автообновление — ставить установку поверх работающей записи.
    ///
    /// Вторая: остановить эту операцию, не зная, чья она. Понадобилось закрытию окна.
    /// Раньше крестик закрывал программу молча, и запись обрывалась вместе с процессом:
    /// уборка за отменой живёт в блоке finally самой операции, а при выходе он не
    /// выполняется — на диске оставались недописанные файлы, неотличимые с виду от готовых.
    /// Отменить оттуда напрямую нельзя: команда отмены есть у каждой вкладки своя, а окно
    /// про вкладки ничего не знает. Поэтому вкладка, начиная работу, оставляет здесь способ
    /// её прервать, а окно этим способом пользуется и дожидается, пока уборка закончится.
    /// </summary>
    public partial class OperationLockService : ObservableObject
    {
        public static OperationLockService Instance { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TaskbarState))]
        [NotifyPropertyChangedFor(nameof(TaskbarProgress))]
        private bool isBusy;

        public bool IsNotBusy => !IsBusy;

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

        /// <summary>
        /// Ход текущей операции долей от единицы — в таком виде его ждёт панель задач.
        /// Ставит вкладка, у которой эта операция идёт; читает полоса на кнопке в панели.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TaskbarProgress))]
        private double progress;

        /// <summary>Операция на паузе: полоса в панели задач становится жёлтой.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TaskbarState))]
        private bool isPaused;

        /// <summary>
        /// Последняя работа кончилась не тем, чего ждали, — полоса остаётся красной,
        /// пока человек не вернётся к окну или не начнёт следующую.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TaskbarState))]
        [NotifyPropertyChangedFor(nameof(TaskbarProgress))]
        private bool lastRunFailed;

        /// <summary>
        /// Сколько закрашивать на кнопке в панели задач.
        ///
        /// Обычно это ход работы, но у законченной неудачи он свой: красный цвет Windows
        /// рисует только по закрашенной части полосы, а к концу операции ход уже обнулён —
        /// красным оказалось бы ровно ничего, то есть кнопка выглядела бы как при удачном
        /// завершении. Поэтому неудача закрашивает полосу целиком.
        /// </summary>
        public double TaskbarProgress => LastRunFailed && !IsBusy ? 1.0 : Progress;

        /// <summary>
        /// Что показывать на кнопке в панели задач.
        ///
        /// Нужна, потому что операции здесь идут десятками минут, и окно на это время
        /// сворачивают. Раньше свёрнутое окно не сообщало ровно ничего: чтобы узнать,
        /// работает программа или давно закончила, приходилось её разворачивать.
        ///
        /// Неудача важнее занятости, хотя занятость на этот момент ещё не снята: итог
        /// становится известен до того, как закончится сама команда — она ещё показывает
        /// окно с сообщением и ждёт ответа. Если бы красная полоса ждала конца команды,
        /// человек увидел бы её только вернувшись к окну, то есть ровно тогда, когда она
        /// уже не нужна. Во время настоящей работы этот случай невозможен: признак
        /// сбрасывается в начале каждой операции.
        /// </summary>
        public TaskbarItemProgressState TaskbarState =>
            LastRunFailed ? TaskbarItemProgressState.Error
                          : IsBusy ? (IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal)
                                   : TaskbarItemProgressState.None;

        /// <summary>Как прервать идущую операцию — без вопросов и подтверждений.</summary>
        private Action? _cancel;

        /// <summary>Завершается, когда операция дошла до конца и убрала за собой.</summary>
        private TaskCompletionSource? _finished;

        private OperationLockService() { }

        /// <summary>
        /// Операция началась. <paramref name="cancel"/> обязан отменять молча: подтверждение
        /// у кнопки «Отмена» своё, и спрашивать второй раз, когда решение уже принято
        /// (например, при закрытии окна), было бы издевательством.
        /// </summary>
        public void OperationStarted(Action cancel)
        {
            _cancel = cancel;
            _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Progress = 0;
            IsPaused = false;
            LastRunFailed = false;
            IsBusy = true;

            // Отсюда же, а не из каждой вкладки: «идёт операция» — ровно то условие,
            // при котором системе нельзя засыпать, и держать его в одном месте надёжнее,
            // чем в четырёх.
            SleepBlocker.KeepAwake();
        }

        /// <summary>
        /// Операция кончилась — чем угодно: успехом, ошибкой, отменой. Вызывается из того же
        /// finally, что снимает занятость, поэтому ожидающий закрытия дождётся её в любом
        /// случае, а не только при удачном завершении.
        /// </summary>
        public void OperationFinished()
        {
            IsBusy = false;
            IsPaused = false;
            Progress = 0;
            _cancel = null;

            SleepBlocker.AllowSleep();

            var finished = _finished;
            _finished = null;
            finished?.TrySetResult();
        }

        /// <summary>
        /// Чем кончилась работа. Вызывается там, где итог уже известен, а не в блоке
        /// finally: в finally его пришлось бы протаскивать через локальную переменную
        /// в каждой вкладке, и однажды кто-нибудь забыл бы это сделать. Если не вызвать
        /// вовсе, полоса просто погаснет — безопасное умолчание, а не ложная тревога.
        /// </summary>
        public void ReportResult(bool succeeded) => LastRunFailed = !succeeded;

        /// <summary>
        /// Человек вернулся к окну — красную полосу можно убирать: она звала его именно
        /// сюда, и звать дальше незачем.
        /// </summary>
        public void ForgetLastResult() => LastRunFailed = false;

        /// <summary>
        /// Прервать идущую операцию и дождаться, пока она уберёт за собой недописанное.
        /// Если ничего не идёт — возвращается сразу.
        /// </summary>
        public Task StopAndWaitAsync()
        {
            var finished = _finished;
            if (finished is null) return Task.CompletedTask;

            _cancel?.Invoke();
            return finished.Task;
        }
    }
}
