using System.Threading;
using System.Threading.Tasks;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Позволяет приостанавливать и возобновлять длительную операцию (разбиение/сборку)
    /// без её прерывания. Поток чтения/записи периодически вызывает WaitIfPausedAsync —
    /// пока не будет Resume(), выполнение там просто ждёт (не блокируя поток синхронно).
    /// Отмена (CancellationToken) продолжает работать даже во время паузы.
    /// </summary>
    public sealed class PauseController
    {
        private readonly object _gate = new();

        private TaskCompletionSource<bool> _resumeSignal = CreateSignaledTcs();

        /// <summary>volatile: флаг читается из рабочего потока операции, а меняется из потока интерфейса.</summary>
        private volatile bool _isPaused;

        public bool IsPaused => _isPaused;

        private static TaskCompletionSource<bool> CreateSignaledTcs()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult(true);
            return tcs;
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_isPaused) return;
                _isPaused = true;
                _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (!_isPaused) return;
                _isPaused = false;
                _resumeSignal.TrySetResult(true);
            }
        }

        /// <summary>
        /// Ждёт снятия паузы. Если во время ожидания пришла отмена — выбрасывает
        /// <see cref="System.OperationCanceledException"/>, как и любая другая
        /// отменяемая операция.
        /// </summary>
        public async Task WaitIfPausedAsync(CancellationToken ct)
        {
            Task signal;
            lock (_gate)
            {
                signal = _resumeSignal.Task;
            }

            if (signal.IsCompletedSuccessfully) return;

            // WaitAsync, а не отмена самого сигнала. Раньше отмена делала TrySetCanceled
            // на общем TaskCompletionSource, и он оставался отменённым навсегда: Resume()
            // уже не мог его переустановить, а следующее ожидание видело «задача
            // завершена» и молча пропускало паузу вместо того, чтобы бросить отмену.
            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
