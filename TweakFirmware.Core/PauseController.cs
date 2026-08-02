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
        private TaskCompletionSource<bool> _resumeSignal = CreateSignaledTcs();

        public bool IsPaused { get; private set; }

        private static TaskCompletionSource<bool> CreateSignaledTcs()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult(true);
            return tcs;
        }

        public void Pause()
        {
            if (IsPaused) return;
            IsPaused = true;
            _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Resume()
        {
            if (!IsPaused) return;
            IsPaused = false;
            _resumeSignal.TrySetResult(true);
        }

        public async Task WaitIfPausedAsync(CancellationToken ct)
        {
            var tcs = _resumeSignal;
            if (tcs.Task.IsCompleted) return;

            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }
    }
}
