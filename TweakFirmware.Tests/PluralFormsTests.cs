using TweakFirmware.Core.Localization;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Русское правило склонения при числе глазами не проверяется: «21 файл», но
    /// «11 файлов», хотя оба оканчиваются на единицу. Сейчас в программе больше пяти
    /// файлов сравнить нельзя, но правило от этого не перестаёт быть общим — и если
    /// предел вырастет, ошибка вылезет не там, где её будут искать.
    ///
    /// Проверяем чистый выбор формы: он берёт язык параметром и текущий язык программы
    /// не трогает, поэтому тесты не мешают друг другу.
    /// </summary>
    public class PluralFormsTests
    {
        [Theory]
        [InlineData(1, PluralForm.One)]
        [InlineData(2, PluralForm.Few)]
        [InlineData(3, PluralForm.Few)]
        [InlineData(4, PluralForm.Few)]
        [InlineData(5, PluralForm.Many)]
        [InlineData(10, PluralForm.Many)]
        [InlineData(0, PluralForm.Many)]
        public void Russian_SmallNumbers(int count, PluralForm expected)
        {
            Assert.Equal(expected, PluralForms.Select(count, "ru"));
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(14)]
        public void Russian_ElevenToFourteen_AreMany_DespiteTheirLastDigit(int count)
        {
            Assert.Equal(PluralForm.Many, PluralForms.Select(count, "ru"));
        }

        [Theory]
        [InlineData(21, PluralForm.One)]
        [InlineData(22, PluralForm.Few)]
        [InlineData(25, PluralForm.Many)]
        [InlineData(101, PluralForm.One)]
        [InlineData(111, PluralForm.Many)]
        [InlineData(112, PluralForm.Many)]
        [InlineData(122, PluralForm.Few)]
        public void Russian_LargeNumbers_FollowTheLastTwoDigits(int count, PluralForm expected)
        {
            Assert.Equal(expected, PluralForms.Select(count, "ru"));
        }

        [Theory]
        [InlineData(1, PluralForm.One)]
        [InlineData(2, PluralForm.Many)]
        [InlineData(5, PluralForm.Many)]
        [InlineData(21, PluralForm.Many)]
        [InlineData(0, PluralForm.Many)]
        public void English_HasOnlyTwoForms(int count, PluralForm expected)
        {
            Assert.Equal(expected, PluralForms.Select(count, "en"));
        }

        [Fact]
        public void UnknownLanguage_FallsBackToRussian()
        {
            // Как и Strings.Get: неизвестный код языка — это русский.
            Assert.Equal(PluralForm.Few, PluralForms.Select(2, "de"));
        }

        [Fact]
        public void NegativeCount_IsCountedByItsMagnitude()
        {
            // Отрицательного числа файлов не бывает, но и падать на нём незачем.
            Assert.Equal(PluralForm.Few, PluralForms.Select(-2, "ru"));
        }
    }
}
