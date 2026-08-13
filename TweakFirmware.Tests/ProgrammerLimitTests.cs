using System;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Границы лимита программатора. Проверяются счётом, а не нарезкой: чтобы получить
    /// вторую часть на настоящем лимите TNM5000, нужен файл в 3,9 ГиБ, а такой прогон
    /// в обычном наборе тестов держать нельзя — он один займёт больше времени и места,
    /// чем весь остальной набор. Арифметика при этом та же самая: именно
    /// CalculateExpectedPartCount решает, сколько файлов получится, и его результат
    /// сверен с фактической нарезкой отдельным тестом в FileSplitterMergerTests.
    /// </summary>
    public class ProgrammerLimitTests
    {
        /// <summary>
        /// Лимит TNM5000 записан в байтах, а в руководстве программатора он указан
        /// в килобайтах. Число зафиксировано тестом: пересчёт «на глаз» при следующей
        /// правке дал бы части, которые программатор не примет.
        /// </summary>
        [Fact]
        public void TnmLimit_IsExactly4055040KiB()
        {
            Assert.Equal(4_055_040L * 1024L, FileSplitter.DefaultMaxPartSizeBytes);
            Assert.Equal(4_152_360_960L, FileSplitter.DefaultMaxPartSizeBytes);
        }

        [Fact]
        public void OneByteBelowLimit_FitsInSingleFile()
        {
            long limit = FileSplitter.DefaultMaxPartSizeBytes;

            Assert.Equal(1, FileSplitter.CalculateExpectedPartCount(limit - 1, limit));
        }

        [Fact]
        public void ExactlyLimit_FitsInSingleFile()
        {
            long limit = FileSplitter.DefaultMaxPartSizeBytes;

            Assert.Equal(1, FileSplitter.CalculateExpectedPartCount(limit, limit));
        }

        [Fact]
        public void OneByteOverLimit_NeedsSecondFile()
        {
            long limit = FileSplitter.DefaultMaxPartSizeBytes;

            Assert.Equal(2, FileSplitter.CalculateExpectedPartCount(limit + 1, limit));
        }

        /// <summary>
        /// Восьмигигабайтный дамп eMMC — обычный размер для этой программы. Счёт идёт
        /// в long: в int такой размер в байтах не помещается вовсе.
        /// </summary>
        [Fact]
        public void TypicalEmmcDump_SplitsIntoTwoFiles()
        {
            long limit = FileSplitter.DefaultMaxPartSizeBytes;
            long eightGiB = 8L * 1024 * 1024 * 1024;

            Assert.Equal(2, FileSplitter.CalculateExpectedPartCount(eightGiB, limit));
        }

        /// <summary>
        /// Ровно кратный размер не даёт лишней пустой части — та же граница, что
        /// и у нарезки настоящего файла, только без записи гигабайтов на диск.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        public void ExactMultiplesOfLimit_ProduceExactlyThatManyFiles(int multiple)
        {
            long limit = FileSplitter.DefaultMaxPartSizeBytes;

            Assert.Equal(multiple, FileSplitter.CalculateExpectedPartCount(limit * multiple, limit));
            Assert.Equal(multiple + 1, FileSplitter.CalculateExpectedPartCount(limit * multiple + 1, limit));
        }

        /// <summary>
        /// Пользовательский лимит из поля ввода проходит тот же путь, что и лимит
        /// пресета: сначала разбор строки, потом счёт частей. Проверяем связку целиком —
        /// по отдельности обе половины уже покрыты, а вместе они считают то, что человек
        /// видит в «Общей информации».
        /// </summary>
        [Fact]
        public void CustomLimitFromTextField_CountsTheSameWay()
        {
            long limit = PartSizeLimit.Resolve(presetLimitBytes: null, customLimitText: "500 000 000");

            Assert.Equal(500_000_000L, limit);
            Assert.Equal(3, FileSplitter.CalculateExpectedPartCount(1_500_000_001L, limit));
        }

        /// <summary>
        /// Переключение «пресет → пользовательский → пресет». Значение в поле остаётся
        /// от предыдущего выбора, и лимит пресета обязан его перебивать: иначе выключенное
        /// поле продолжало бы влиять на нарезку.
        /// </summary>
        [Fact]
        public void SwitchingBackToPreset_IgnoresLeftoverCustomText()
        {
            const string leftover = "1024";

            long preset = PartSizeLimit.Resolve(FileSplitter.DefaultMaxPartSizeBytes, leftover);
            long custom = PartSizeLimit.Resolve(presetLimitBytes: null, customLimitText: leftover);
            long backToPreset = PartSizeLimit.Resolve(FileSplitter.DefaultMaxPartSizeBytes, leftover);

            Assert.Equal(FileSplitter.DefaultMaxPartSizeBytes, preset);
            Assert.Equal(1024, custom);
            Assert.Equal(FileSplitter.DefaultMaxPartSizeBytes, backToPreset);
        }
    }
}
