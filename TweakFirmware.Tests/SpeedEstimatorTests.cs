using System;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Оценка «сколько осталось» глазами не проверяется: чтобы увидеть, что она врёт,
    /// нужно сначала полчаса подождать. Поэтому часы у оценщика передаются снаружи,
    /// и весь ряд отсчётов — от раскачки до паузы — прогоняется здесь за миллисекунды.
    /// </summary>
    public class SpeedEstimatorTests
    {
        private const long Mb = 1024L * 1024;

        /// <summary>Ровная работа: каждую секунду записано столько-то.</summary>
        private static SpeedEstimator Steady(long bytesPerSecond, int seconds, double startSecond = 0)
        {
            var speed = new SpeedEstimator();
            for (int i = 0; i <= seconds; i++)
                speed.Add(TimeSpan.FromSeconds(startSecond + i), bytesPerSecond * i);
            return speed;
        }

        [Fact]
        public void WithoutSamples_ThereIsNothingToSay()
        {
            var speed = new SpeedEstimator();

            Assert.Null(speed.BytesPerSecond);
            Assert.Null(speed.Remaining(100 * Mb));
        }

        [Fact]
        public void OneSample_IsNotEnough()
        {
            var speed = new SpeedEstimator();
            speed.Add(TimeSpan.Zero, 0);

            Assert.Null(speed.BytesPerSecond);
        }

        [Fact]
        public void FirstFractionOfASecond_IsNotMeasuredAtAll()
        {
            // На долях секунды скорость скачет в разы, и показывать её — только
            // мельтешить числами.
            var speed = new SpeedEstimator();
            speed.Add(TimeSpan.Zero, 0);
            speed.Add(TimeSpan.FromMilliseconds(500), 50 * Mb);

            Assert.Null(speed.BytesPerSecond);
        }

        [Fact]
        public void SteadyWork_IsMeasuredAsItIs()
        {
            var speed = Steady(100 * Mb, seconds: 4);

            Assert.NotNull(speed.BytesPerSecond);
            Assert.InRange(speed.BytesPerSecond!.Value, 99 * Mb, 101 * Mb);
        }

        [Fact]
        public void OnlyTheLastSecondsCount_SoASlowStartIsForgotten()
        {
            // Ради этого оценка и считается по окну: первые секунды уходят на раскачку,
            // и среднее от начала работы к концу часовой операции врёт заметнее, чем
            // в начале.
            var speed = new SpeedEstimator();

            long done = 0;
            for (int second = 1; second <= 20; second++)
            {
                done += second <= 10 ? 10 * Mb : 200 * Mb;
                speed.Add(TimeSpan.FromSeconds(second), done);
            }

            Assert.NotNull(speed.BytesPerSecond);
            Assert.InRange(speed.BytesPerSecond!.Value, 199 * Mb, 201 * Mb);
        }

        [Fact]
        public void StandingStill_LooksLikeNothingToSay_NotLikeZeroSpeed()
        {
            // Так выглядит пауза: отсчёты идут, а байты не растут. Ноль в делителе
            // оставшегося времени дал бы бесконечность.
            var speed = new SpeedEstimator();
            for (int second = 0; second <= 3; second++)
                speed.Add(TimeSpan.FromSeconds(second), 500 * Mb);

            Assert.Null(speed.BytesPerSecond);
            Assert.Null(speed.Remaining(1000 * Mb));
        }

        [Fact]
        public void LongGap_StartsTheCountAnew()
        {
            // Пауза выглядит именно так: отсчётов не было вовсе, потому что писать было
            // некому. Если бы простой попал в окно, скорость после возобновления
            // показывалась бы вдвое ниже настоящей — и ещё несколько секунд подряд.
            var speed = Steady(100 * Mb, seconds: 4);

            speed.Add(TimeSpan.FromSeconds(300), 400 * Mb);
            Assert.Null(speed.BytesPerSecond);

            speed.Add(TimeSpan.FromSeconds(302), 600 * Mb);
            Assert.NotNull(speed.BytesPerSecond);
            Assert.InRange(speed.BytesPerSecond!.Value, 99 * Mb, 101 * Mb);
        }

        [Fact]
        public void CountersGoingBackwards_StartTheCountAnew()
        {
            // Так выглядит повторно запущенная операция, если оценщик не сбросили.
            var speed = Steady(100 * Mb, seconds: 4);

            speed.Add(TimeSpan.FromSeconds(5), 0);
            Assert.Null(speed.BytesPerSecond);
        }

        [Fact]
        public void Remaining_IsWhatIsLeftAtTheCurrentRate()
        {
            var speed = Steady(100 * Mb, seconds: 4);

            var left = speed.Remaining(1000 * Mb);

            Assert.NotNull(left);
            Assert.InRange(left!.Value.TotalSeconds, 5.9, 6.1);
        }

        [Fact]
        public void Remaining_IsSilentAtTheVeryEnd()
        {
            // «Осталось 0 секунд» под полосой на сотне процентов — шум, а не сведения.
            var speed = Steady(100 * Mb, seconds: 4);

            Assert.Null(speed.Remaining(400 * Mb));
        }

        [Fact]
        public void ImplausiblyLongEstimate_IsNotShownAtAll()
        {
            // Такое получается, когда скорость ещё не устоялась. «Осталось 940 ч» хуже,
            // чем ничего: полосе после такого не верят вообще.
            var speed = new SpeedEstimator();
            speed.Add(TimeSpan.Zero, 0);
            speed.Add(TimeSpan.FromSeconds(2), 1024);

            Assert.Null(speed.Remaining(1024L * 1024 * 1024 * 1024));
        }

        [Fact]
        public void Reset_ForgetsEverything()
        {
            var speed = Steady(100 * Mb, seconds: 4);
            speed.Reset();

            Assert.Null(speed.BytesPerSecond);
        }
    }
}
