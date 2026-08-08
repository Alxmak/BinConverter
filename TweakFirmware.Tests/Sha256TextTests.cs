using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    public class Sha256TextTests
    {
        private const string RealHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        [Fact]
        public void Normalize_RemovesEveryKindOfWhitespace()
        {
            Assert.Equal(RealHash, Sha256Text.Normalize("  " + RealHash + "\r\n"));
            Assert.Equal(RealHash, Sha256Text.Normalize(RealHash.Insert(32, " ")));
            Assert.Equal(RealHash, Sha256Text.Normalize(RealHash.Insert(32, "\t")));
        }

        /// <summary>
        /// Главный случай: наши же окна показывают хэш разбитым на строки (HashDisplay.Wrap),
        /// и скопированный оттуда текст должен приниматься полем «Ожидаемый SHA-256».
        /// </summary>
        [Fact]
        public void WrappedHash_RoundTripsThroughNormalize()
        {
            string wrapped = HashDisplay.Wrap(RealHash);

            Assert.Contains("\n", wrapped);
            Assert.Equal(RealHash, Sha256Text.Normalize(wrapped));
            Assert.True(Sha256Text.LooksValid(wrapped));
        }

        [Fact]
        public void Normalize_HandlesNullAndEmpty()
        {
            Assert.Equal("", Sha256Text.Normalize(null));
            Assert.Equal("", Sha256Text.Normalize(""));
            Assert.Equal("", Sha256Text.Normalize("   \r\n\t "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n")]
        public void IsBlank_TreatsWhitespaceOnlyAsEmpty(string? text)
        {
            Assert.True(Sha256Text.IsBlank(text));
        }

        [Fact]
        public void IsBlank_IsFalseForAnyContent()
        {
            Assert.False(Sha256Text.IsBlank("z"));
            Assert.False(Sha256Text.IsBlank(RealHash));
        }

        [Fact]
        public void LooksValid_AcceptsBothCases()
        {
            Assert.True(Sha256Text.LooksValid(RealHash));
            Assert.True(Sha256Text.LooksValid(RealHash.ToUpperInvariant()));
        }

        [Fact]
        public void LooksValid_RejectsWrongLength()
        {
            Assert.False(Sha256Text.LooksValid(RealHash.Substring(0, 63)));
            Assert.False(Sha256Text.LooksValid(RealHash + "0"));
            Assert.False(Sha256Text.LooksValid(""));
        }

        [Fact]
        public void LooksValid_RejectsNonHexCharacters()
        {
            // 64 знака, но 'g' в шестнадцатеричной записи не бывает — самый частый
            // случай, когда в поле попал не хэш, а что-то другое той же длины.
            Assert.False(Sha256Text.LooksValid(new string('g', Sha256Text.HexLength)));
            Assert.False(Sha256Text.LooksValid(RealHash.Substring(0, 63) + "-"));
        }
    }
}
