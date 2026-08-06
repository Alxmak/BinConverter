using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Сравнение версий решает, предлагать ли обновление. Ошибка здесь тихая: обновления
    /// просто перестанут приходить, приложение продолжит работать, и заметить это можно
    /// только случайно — поэтому граничные случаи зафиксированы тестами.
    /// </summary>
    public class VersionHelperTests
    {
        [Theory]
        [InlineData("2.5.2", "2.5.1")]   // патч
        [InlineData("2.6.0", "2.5.9")]   // минор старше большего патча
        [InlineData("3.0.0", "2.99.99")] // мажор
        [InlineData("2.5.10", "2.5.9")]  // не строковое сравнение: 10 > 9
        [InlineData("2.10.0", "2.9.0")]
        public void IsNewer_TrueForNewerVersions(string candidate, string current)
        {
            Assert.True(VersionHelper.IsNewer(candidate, current));
        }

        [Theory]
        [InlineData("2.5.1", "2.5.1")]   // та же версия — не новее
        [InlineData("2.5.0", "2.5.1")]   // старее
        [InlineData("2.4.9", "2.5.0")]
        [InlineData("1.0.0", "2.0.0")]
        public void IsNewer_FalseForSameOrOlder(string candidate, string current)
        {
            Assert.False(VersionHelper.IsNewer(candidate, current));
        }

        [Theory]
        [InlineData("3", "2.9.9")]       // тег вида "v3"
        [InlineData("2.6", "2.5.9")]     // тег вида "v2.6"
        public void IsNewer_HandlesShortVersionStrings(string candidate, string current)
        {
            Assert.True(VersionHelper.IsNewer(candidate, current));
        }

        [Theory]
        [InlineData("2.6", "2.6.0")]     // "2.6" == "2.6.0"
        [InlineData("2", "2.0.0")]
        public void ShortForm_EqualsPaddedForm(string shortForm, string padded)
        {
            Assert.False(VersionHelper.IsNewer(shortForm, padded));
            Assert.False(VersionHelper.IsNewer(padded, shortForm));
        }

        [Theory]
        [InlineData("не версия", "2.5.0")]
        [InlineData("2.5.0", "не версия")]
        [InlineData("", "2.5.0")]
        [InlineData("2.5.0", "")]
        [InlineData(null, "2.5.0")]
        [InlineData("2.5.0", null)]
        [InlineData("v2.6.0", "2.5.0")]  // префикс 'v' должен быть срезан ДО вызова
        public void IsNewer_FalseWhenEitherSideUnparsable(string? candidate, string? current)
        {
            // Лучше не предложить обновление, чем предложить установку неизвестно чего.
            Assert.False(VersionHelper.IsNewer(candidate, current));
        }

        [Fact]
        public void TryParse_PadsMissingComponents()
        {
            Assert.True(VersionHelper.TryParse("2", out var v1));
            Assert.Equal(new System.Version(2, 0, 0), v1);

            Assert.True(VersionHelper.TryParse("2.6", out var v2));
            Assert.Equal(new System.Version(2, 6, 0), v2);

            Assert.True(VersionHelper.TryParse("2.6.1", out var v3));
            Assert.Equal(new System.Version(2, 6, 1), v3);
        }

        [Fact]
        public void TryParse_FourComponentVersionIsAccepted()
        {
            // AssemblyVersion в проекте четырёхкомпонентный (2.5.2.0).
            Assert.True(VersionHelper.TryParse("2.5.2.0", out var v));
            Assert.Equal(new System.Version(2, 5, 2, 0), v);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("abc")]
        [InlineData("2.x.1")]
        public void TryParse_FalseWithoutThrowing(string? text)
        {
            // Прежний код в UpdateManager использовал конструктор Version, который на такой
            // строке бросал исключение вместо возврата false.
            Assert.False(VersionHelper.TryParse(text, out _));
        }
    }
}
