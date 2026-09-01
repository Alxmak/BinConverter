using System;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Подпись под полосой прогресса. Проверяем не сами слова — они зависят от языка,
    /// а он в программе переключается, — а то, что выбрана верная ветка и части стоят
    /// в верном порядке.
    /// </summary>
    public class ProgressCaptionTests
    {
        private const double Kb = 1024.0;
        private const double Mb = 1024.0 * 1024;
        private const double Gb = 1024.0 * 1024 * 1024;

        [Fact]
        public void WithoutEstimates_CaptionStaysWhatItWas()
        {
            // До первых отсчётов считать нечего, и подпись должна остаться ровно той,
            // что была здесь до появления оценки.
            Assert.Equal("файл 3 из 12", ProgressCaption.Build("файл 3 из 12", null, null));
        }

        [Fact]
        public void RemainingComesBeforeSpeed()
        {
            // «Осталось» — ответ на вопрос, ради которого сюда смотрят; скорость только
            // объясняет, откуда это число взялось.
            string caption = ProgressCaption.Build("файл 3 из 12", 180 * Mb, TimeSpan.FromMinutes(12));

            string remaining = ProgressCaption.FormatDuration(TimeSpan.FromMinutes(12));
            string speed = ProgressCaption.FormatSpeed(180 * Mb);

            Assert.StartsWith("файл 3 из 12", caption, StringComparison.Ordinal);
            Assert.Contains(remaining, caption, StringComparison.Ordinal);
            Assert.Contains(speed, caption, StringComparison.Ordinal);
            Assert.True(caption.IndexOf(remaining, StringComparison.Ordinal)
                        < caption.IndexOf(speed, StringComparison.Ordinal));
        }

        [Fact]
        public void EmptyBase_DoesNotLeaveALeadingSeparator()
        {
            // У «Извлечения разделов» подпись — название прохода, и в начале работы
            // его может ещё не быть.
            string caption = ProgressCaption.Build("", 180 * Mb, null);

            Assert.Equal(ProgressCaption.FormatSpeed(180 * Mb), caption);
        }

        [Fact]
        public void LessThanAMinute_HasItsOwnWording()
        {
            // «0 мин» — не ответ, а «40 сек» под полосой мельтешит на каждом отсчёте.
            Assert.Equal(Strings.Get("Common_DurationUnderMinute"),
                         ProgressCaption.FormatDuration(TimeSpan.FromSeconds(40)));
        }

        [Fact]
        public void Minutes_AreRoundedUp()
        {
            // Вниз округлять нельзя: полторы минуты, показанные как одна, кончаются
            // ожиданием вдвое дольше обещанного.
            Assert.Equal(Strings.Format("Common_DurationMinutes", 2),
                         ProgressCaption.FormatDuration(TimeSpan.FromSeconds(90)));
        }

        [Fact]
        public void RoundingUp_CanReachAWholeHour()
        {
            // 59 минут 40 секунд — это «1 ч 00 мин», а не «60 мин»: округление вверх
            // само добирает до часа, и ветка выбирается уже по округлённому числу.
            Assert.Equal(Strings.Format("Common_DurationHours", 1, 0),
                         ProgressCaption.FormatDuration(TimeSpan.FromSeconds(59 * 60 + 40)));
        }

        [Fact]
        public void Hours_KeepTheMinutesToo()
        {
            Assert.Equal(Strings.Format("Common_DurationHours", 2, 5),
                         ProgressCaption.FormatDuration(TimeSpan.FromMinutes(125)));
        }

        [Fact]
        public void Speed_PicksTheUnitByItsMagnitude()
        {
            Assert.Equal(Strings.Format("Common_SpeedKb", 500.0), ProgressCaption.FormatSpeed(500 * Kb));
            Assert.Equal(Strings.Format("Common_SpeedMb", 180.0), ProgressCaption.FormatSpeed(180 * Mb));
            Assert.Equal(Strings.Format("Common_SpeedGb", 1.5), ProgressCaption.FormatSpeed(1.5 * Gb));
        }

        [Fact]
        public void SlowestSpeeds_StillHaveAUnit()
        {
            // Сетевая папка может отдавать килобайты в секунду — на «0 МБ/с» это
            // выглядело бы как остановка.
            Assert.Equal(Strings.Format("Common_SpeedKb", 1.0), ProgressCaption.FormatSpeed(Kb));
        }
    }
}
