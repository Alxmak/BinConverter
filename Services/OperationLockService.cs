using System;
using System.Threading.Tasks;
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
        private bool isBusy;

        public bool IsNotBusy => !IsBusy;

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

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
            _cancel = null;

            SleepBlocker.AllowSleep();

            var finished = _finished;
            _finished = null;
            finished?.TrySetResult();
        }

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
