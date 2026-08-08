using System;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    public class PauseControllerTests
    {
        /// <summary>Столько ждём событий, которые обязаны произойти почти сразу.</summary>
        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task NotPaused_ReturnsImmediately()
        {
            var controller = new PauseController();

            await controller.WaitIfPausedAsync(CancellationToken.None);

            Assert.False(controller.IsPaused);
        }

        [Fact]
        public async Task Paused_WaitsUntilResumed()
        {
            var controller = new PauseController();
            controller.Pause();

            var waiting = controller.WaitIfPausedAsync(CancellationToken.None);
            Assert.False(waiting.IsCompleted);

            controller.Resume();

            await waiting.WaitAsync(Soon);
            Assert.False(controller.IsPaused);
        }

        [Fact]
        public async Task CancelledWhilePaused_Throws()
        {
            var controller = new PauseController();
            controller.Pause();
            using var cts = new CancellationTokenSource();

            var waiting = controller.WaitIfPausedAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        /// <summary>
        /// Тот самый случай, который был сломан. Отмена делала TrySetCanceled на общем
        /// сигнале, и он оставался отменённым навсегда: Resume уже не мог его
        /// переустановить, а следующее ожидание видело «задача завершена» и молча
        /// проходило мимо паузы вместо ожидания.
        /// </summary>
        [Fact]
        public async Task PauseStillWorksAfterACancelledWait()
        {
            var controller = new PauseController();
            controller.Pause();

            using (var cts = new CancellationTokenSource())
            {
                var cancelled = controller.WaitIfPausedAsync(cts.Token);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            }

            // Снимаем и ставим паузу заново — ожидание обязано снова ждать.
            controller.Resume();
            controller.Pause();

            var waiting = controller.WaitIfPausedAsync(CancellationToken.None);
            Assert.False(waiting.IsCompleted);

            controller.Resume();
            await waiting.WaitAsync(Soon);
        }

        [Fact]
        public async Task ResumeWithoutPause_ChangesNothing()
        {
            var controller = new PauseController();

            controller.Resume();

            await controller.WaitIfPausedAsync(CancellationToken.None);
            Assert.False(controller.IsPaused);
        }

        [Fact]
        public async Task PauseTwice_NeedsOnlyOneResume()
        {
            var controller = new PauseController();
            controller.Pause();
            controller.Pause();

            var waiting = controller.WaitIfPausedAsync(CancellationToken.None);
            controller.Resume();

            await waiting.WaitAsync(Soon);
        }

        [Fact]
        public async Task SeveralWaitersAllResume()
        {
            // В самой операции ожидающий один, но состояние сигнала не должно зависеть
            // от того, сколько раз его дождались.
            var controller = new PauseController();
            controller.Pause();

            var first = controller.WaitIfPausedAsync(CancellationToken.None);
            var second = controller.WaitIfPausedAsync(CancellationToken.None);

            controller.Resume();

            await Task.WhenAll(first, second).WaitAsync(Soon);
        }

        [Fact]
        public async Task AlreadyCancelledToken_ThrowsWhenPaused()
        {
            var controller = new PauseController();
            controller.Pause();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => controller.WaitIfPausedAsync(cts.Token));
        }

        [Fact]
        public async Task AlreadyCancelledToken_IsIgnoredWhenNotPaused()
        {
            // Пауза не стоит — ждать нечего, и отмену здесь проверять не наше дело:
            // её проверяет сам цикл операции.
            var controller = new PauseController();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await controller.WaitIfPausedAsync(cts.Token);
        }
    }
}
