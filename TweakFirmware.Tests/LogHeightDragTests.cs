using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Растягивание журнала ручкой. Проверки идут числами, потому что глазами эта
    /// часть не разбирается: неверный счёт выглядит не как «не тот размер», а как
    /// дрожание — журнал скачет между двумя размерами, пока мышь едет в одну сторону.
    /// </summary>
    public class LogHeightDragTests
    {
        /// <summary>Высота строки. Круглая — чтобы в проверках были видны сами числа, а не арифметика.</summary>
        private const double Row = 40;

        private const int Min = 1;
        private const int Max = 10;

        private static LogHeightDrag FromFiveLines() => new(5 * Row, Row, Min, Max);

        /// <summary>
        /// Главная проверка: ровный ход мыши вниз не должен ни разу сжать журнал.
        /// Прежний счёт разворачивал его на строку, а следующим же смещением
        /// сворачивал обратно — сюда это и попадало бы.
        /// </summary>
        [Fact]
        public void SteadyPullDown_NeverShrinksOnTheWay()
        {
            var drag = FromFiveLines();

            int previous = 5;
            for (int px = 0; px <= (int)(Max * Row); px++)
            {
                int lines = drag.LinesFor(px);
                Assert.True(lines >= previous, $"на {px} px журнал сжался с {previous} строк до {lines}");
                previous = lines;
            }

            Assert.Equal(Max, previous);
        }

        /// <summary>То же в обратную сторону: ровный ход вверх не должен журнал растягивать.</summary>
        [Fact]
        public void SteadyPullUp_NeverGrowsOnTheWay()
        {
            var drag = FromFiveLines();

            int previous = 5;
            for (int px = 0; px <= (int)(Max * Row); px++)
            {
                int lines = drag.LinesFor(-px);
                Assert.True(lines <= previous, $"на -{px} px журнал вырос с {previous} строк до {lines}");
                previous = lines;
            }

            Assert.Equal(Min, previous);
        }

        /// <summary>Мышь вернулась туда, где её нажали — размер должен вернуться исходный.</summary>
        [Fact]
        public void MouseBackAtTheGrabPoint_RestoresTheStartingSize()
        {
            var drag = FromFiveLines();

            drag.LinesFor(3 * Row);

            Assert.Equal(5, drag.LinesFor(0));
        }

        /// <summary>Ход мыши переводится в строки один к одному, пока не упёрся в границы.</summary>
        [Theory]
        [InlineData(0.0, 5)]
        [InlineData(Row, 6)]
        [InlineData(2 * Row, 7)]
        [InlineData(-Row, 4)]
        [InlineData(-3 * Row, 2)]
        public void MouseTravel_TurnsIntoLinesOneForOne(double mouseOffset, int expected)
        {
            Assert.Equal(expected, FromFiveLines().LinesFor(mouseOffset));
        }

        /// <summary>Дальше границ журнал не растягивается и не сжимается.</summary>
        [Fact]
        public void LimitsAreNeverCrossed()
        {
            Assert.Equal(Max, FromFiveLines().LinesFor(100 * Row));
            Assert.Equal(Min, FromFiveLines().LinesFor(-100 * Row));
        }

        /// <summary>
        /// Мышь увели далеко за нижнюю границу и повели обратно: журнал обязан тронуться
        /// сразу, а не после того, как мышь отработает лишний ход.
        /// </summary>
        [Fact]
        public void ComingBackFromTheBottomLimit_MovesAtOnce()
        {
            var drag = FromFiveLines();

            Assert.Equal(Max, drag.LinesFor(20 * Row));
            Assert.Equal(Max, drag.LinesFor(20 * Row));
            Assert.Equal(Max - 1, drag.LinesFor(19 * Row));
        }

        /// <summary>То же у верхней границы.</summary>
        [Fact]
        public void ComingBackFromTheTopLimit_MovesAtOnce()
        {
            var drag = FromFiveLines();

            Assert.Equal(Min, drag.LinesFor(-20 * Row));
            Assert.Equal(Min, drag.LinesFor(-20 * Row));
            Assert.Equal(Min + 1, drag.LinesFor(-19 * Row));
        }

        /// <summary>
        /// Мышь стоит у самой границы размеров и подрагивает на пиксель — размер меняться
        /// не должен. Без запаса сверх половины строки журнал мигал бы между соседними.
        /// </summary>
        [Fact]
        public void WobbleNearTheSwitchPoint_KeepsTheSize()
        {
            var drag = FromFiveLines();
            Assert.Equal(6, drag.LinesFor(Row));

            for (double wobble = -1; wobble <= 1; wobble += 0.5)
                Assert.Equal(6, drag.LinesFor(Row * 1.55 + wobble));
        }

        /// <summary>Но запас — именно запас: увели дальше, и размер меняется.</summary>
        [Fact]
        public void PastTheSwitchPoint_TheSizeChanges()
        {
            var drag = FromFiveLines();
            Assert.Equal(6, drag.LinesFor(Row));

            Assert.Equal(7, drag.LinesFor(Row * 1.7));
        }

        /// <summary>
        /// Высота при захвате может не быть кратной строке (округление разметки) —
        /// счёт всё равно должен начинаться с ближайшего целого числа строк.
        /// </summary>
        [Fact]
        public void UnevenStartingHeight_IsTakenAsTheNearestLineCount()
        {
            var drag = new LogHeightDrag(5 * Row + 1, Row, Min, Max);

            Assert.Equal(6, drag.LinesFor(Row));
        }
    }
}
