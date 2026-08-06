using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Лимит на один файл определяет, на сколько частей разрежется прошивка и какого они
    /// будут размера. Поле показывает число с разделителями разрядов, поэтому разбор строки
    /// здесь не тривиален, а проверять его на живом файле в несколько гигабайт — дорого.
    /// </summary>
    public class PartSizeLimitTests
    {
        // ============ Приоритет пресета ============

        [Fact]
        public void Resolve_FixedPreset_WinsOverTextField()
        {
            // У готовых моделей программаторов лимит фиксирован: поле недоступно и его
            // содержимое не должно влиять на результат, даже если там мусор.
            Assert.Equal(500_000_000, PartSizeLimit.Resolve(500_000_000, "мусор"));
            Assert.Equal(500_000_000, PartSizeLimit.Resolve(500_000_000, ""));
            Assert.Equal(500_000_000, PartSizeLimit.Resolve(500_000_000, "123"));
        }

        [Fact]
        public void Resolve_CustomPreset_UsesTextField()
        {
            Assert.Equal(123456, PartSizeLimit.Resolve(null, "123456"));
        }

        [Fact]
        public void Resolve_CustomPreset_ParsesGroupedDigits()
        {
            // Именно так значение выглядит в поле после форматирования.
            Assert.Equal(500_000_000, PartSizeLimit.Resolve(null, "500,000,000"));
            Assert.Equal(500_000_000, PartSizeLimit.Resolve(null, "500 000 000"));
            Assert.Equal(500_000_000, PartSizeLimit.Resolve(null, "500\u00A0000\u00A0000")); // неразрывные пробелы
        }

        // ============ Значения, которые лимитом быть не могут ============

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("абв")]
        [InlineData("0")]
        [InlineData("0,000")]
        public void Resolve_ReturnsZeroForUnusableInput(string? text)
        {
            Assert.Equal(0, PartSizeLimit.Resolve(null, text));
        }

        [Fact]
        public void Resolve_ZeroMeansNotSet()
        {
            // Ноль — договорённость с вызывающим кодом: предпросмотр не строится.
            Assert.Equal(0, PartSizeLimit.Resolve(null, "0"));
            Assert.Equal(0, PartSizeLimit.Resolve(null, ""));
        }

        [Fact]
        public void Resolve_MinusSignIsStrippedNotHonoured()
        {
            // Поле принимает только цифры (PreviewTextInput), поэтому минус сюда попасть
            // не должен. Фиксируем фактическое поведение: знак отбрасывается, а не даёт
            // отрицательный лимит, который сломал бы расчёт числа частей.
            Assert.Equal(500, PartSizeLimit.Resolve(null, "-500"));
        }

        [Fact]
        public void Resolve_VeryLargeValueDoesNotOverflowToNegative()
        {
            // Длиннее long — разбор не проходит, получаем 0, а не отрицательное число.
            Assert.Equal(0, PartSizeLimit.Resolve(null, "99999999999999999999999"));
        }

        // ============ DigitsOnly ============

        [Theory]
        [InlineData("500,000,000", "500000000")]
        [InlineData("500 000 000", "500000000")]
        [InlineData("abc123def", "123")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("...", "")]
        public void DigitsOnly_KeepsOnlyDigits(string? input, string expected)
        {
            Assert.Equal(expected, PartSizeLimit.DigitsOnly(input));
        }
    }
}
