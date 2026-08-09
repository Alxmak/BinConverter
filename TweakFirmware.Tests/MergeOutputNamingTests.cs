using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Имя собранного файла подставляется в поле «Папка назначения» автоматически, пока
    /// пользователь его не тронул. Ошибка здесь означает запись не туда, куда ожидалось, —
    /// или перезапись самой цепочки частей.
    ///
    /// Пути собираются через <see cref="TestPaths"/>: имя выделяется через
    /// <see cref="System.IO.Path"/>, а тот разбирает путь по правилам той системы,
    /// где запущен.
    /// </summary>
    public class MergeOutputNamingTests
    {
        [Theory]
        [InlineData("emmc.bin", "emmc_merged.bin")]
        [InlineData("имя с пробелами.bin", "имя с пробелами_merged.bin")]
        // Имя без расширения — суффикс просто дописывается в конец.
        [InlineData("emmc", "emmc_merged")]
        // Дампы часто называют "emmc.full.bin": суффикс встаёт перед ".bin", а не ".full".
        [InlineData("emmc.full.bin", "emmc.full_merged.bin")]
        // Повторная сборка уже собранного файла даёт "_merged_merged". Некрасиво, но
        // безопасно: существующий файл не перезаписывается молча.
        [InlineData("emmc_merged.bin", "emmc_merged_merged.bin")]
        public void SuggestFileName_InsertsSuffixBeforeExtension(string fileName, string expected)
        {
            // Результат — всегда имя файла без папки, откуда бы ни пришёл базовый путь.
            Assert.Equal(expected, MergeOutputNaming.SuggestFileName(fileName));
            Assert.Equal(expected, MergeOutputNaming.SuggestFileName(TestPaths.Absolute("D", "dumps", fileName)));
            Assert.Equal(expected, MergeOutputNaming.SuggestFileName(TestPaths.Absolute("C", "a b", fileName)));
        }

        [Fact]
        public void SuggestFileName_ResultDiffersFromSource_SoMergeCannotOverwriteTheChain()
        {
            // Главное свойство: имя обязано отличаться от исходного, иначе сборка писала бы
            // поверх базового файла цепочки, из которого сама же и читает.
            Assert.NotEqual("emmc.bin", MergeOutputNaming.SuggestFileName(TestPaths.Absolute("D", "dumps", "emmc.bin")));
        }

        [Fact]
        public void MergedSuffix_IsPartOfEveryName()
        {
            Assert.Contains(MergeOutputNaming.MergedSuffix, MergeOutputNaming.SuggestFileName("x.bin"));
        }
    }
}
