using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Хэш в диалоге обрезался по ширине окна, потому что 64 знака без пробелов оно
    /// перенести не может. Разбивка на строки — вещь мелкая, но проверять её глазами
    /// значит каждый раз доводить сборку до конца и разглядывать окно.
    /// </summary>
    public class HashDisplayTests
    {
        private const string Sha256 = "E4D5BA2D8CB4C1B6316683844AB640D462990050A056687BB05720AB1C3D4E5F";

        [Fact]
        public void Sha256_SplitsIntoTwoLines()
        {
            string[] lines = HashDisplay.Wrap(Sha256).Split('\n');

            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.Equal(32, l.Length));
        }

        [Fact]
        public void NothingIsLostOrAdded()
        {
            // Главное свойство: убрав переносы, обязаны получить исходный хэш —
            // иначе пользователь сверял бы искажённую сумму.
            Assert.Equal(Sha256, HashDisplay.Wrap(Sha256).Replace("\n", ""));
        }

        [Fact]
        public void NoLineExceedsTheLimit()
        {
            foreach (string line in HashDisplay.Wrap(Sha256, 10).Split('\n'))
                Assert.True(line.Length <= 10, $"строка длиннее предела: {line}");
        }

        [Fact]
        public void ShortValueIsLeftAlone()
        {
            Assert.Equal("ABC", HashDisplay.Wrap("ABC"));
            Assert.DoesNotContain("\n", HashDisplay.Wrap("ABC"));
        }

        [Fact]
        public void ExactlyOneLineLong_GetsNoBreak()
        {
            string exact = new string('A', HashDisplay.LineLength);

            Assert.Equal(exact, HashDisplay.Wrap(exact));
            Assert.DoesNotContain("\n", HashDisplay.Wrap(exact));
        }

        [Fact]
        public void OneCharOverTheLimit_GivesTwoLinesAndNoEmptyOne()
        {
            string value = new string('A', HashDisplay.LineLength + 1);
            string[] lines = HashDisplay.Wrap(value).Split('\n');

            Assert.Equal(2, lines.Length);
            Assert.Equal(HashDisplay.LineLength, lines[0].Length);
            Assert.Single(lines[1]);
        }

        [Fact]
        public void NoTrailingBreak_EvenWhenLengthDividesEvenly()
        {
            // Иначе в диалоге появлялась бы пустая строка под хэшем.
            string value = new string('A', HashDisplay.LineLength * 3);
            string wrapped = HashDisplay.Wrap(value);

            Assert.False(wrapped.EndsWith('\n'));
            Assert.Equal(3, wrapped.Split('\n').Length);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void BlankValue_GivesEmptyString(string? value)
        {
            Assert.Equal("", HashDisplay.Wrap(value));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveLineLength_LeavesTheValueUntouched(int lineLength)
        {
            // Защита от бесконечного цикла: лучше показать неразбитый хэш, чем повиснуть.
            Assert.Equal(Sha256, HashDisplay.Wrap(Sha256, lineLength));
        }
    }
}
