using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Имя собранного файла подставляется в поле «Папка назначения» автоматически, пока
    /// пользователь его не тронул. Ошибка здесь означает запись не туда, куда ожидалось, —
    /// или перезапись самой цепочки частей.
    /// </summary>
    public class MergeOutputNamingTests
    {
        [Theory]
        [InlineData(@"D:\dumps\emmc.bin", "emmc_merged.bin")]
        [InlineData(@"C:\a b\имя с пробелами.bin", "имя с пробелами_merged.bin")]
        [InlineData("emmc.bin", "emmc_merged.bin")]
        public void SuggestFileName_InsertsSuffixBeforeExtension(string basePath, string expected)
        {
            Assert.Equal(expected, MergeOutputNaming.SuggestFileName(basePath));
        }

        [Fact]
        public void SuggestFileName_NoExtension_JustAppendsSuffix()
        {
            Assert.Equal("emmc_merged", MergeOutputNaming.SuggestFileName(@"D:\dumps\emmc"));
        }

        [Fact]
        public void SuggestFileName_SeveralDots_OnlyLastPartIsTreatedAsExtension()
        {
            // Дампы часто называют "emmc.full.bin" — суффикс должен встать перед ".bin",
            // а не перед ".full".
            Assert.Equal("emmc.full_merged.bin", MergeOutputNaming.SuggestFileName(@"D:\emmc.full.bin"));
        }

        [Fact]
        public void SuggestFileName_ResultDiffersFromSource_SoMergeCannotOverwriteTheChain()
        {
            // Главное свойство: имя обязано отличаться от исходного, иначе сборка писала бы
            // поверх базового файла цепочки, из которого сама же и читает.
            const string basePath = @"D:\dumps\emmc.bin";
            Assert.NotEqual("emmc.bin", MergeOutputNaming.SuggestFileName(basePath));
        }

        [Fact]
        public void SuggestFileName_AlreadyMerged_GetsSuffixAgain()
        {
            // Фиксируем фактическое поведение: повторная сборка уже собранного файла даёт
            // "emmc_merged_merged.bin". Некрасиво, но безопасно — существующий файл
            // не перезаписывается молча.
            Assert.Equal("emmc_merged_merged.bin", MergeOutputNaming.SuggestFileName(@"D:\emmc_merged.bin"));
        }

        [Fact]
        public void MergedSuffix_IsPartOfEveryName()
        {
            Assert.Contains(MergeOutputNaming.MergedSuffix, MergeOutputNaming.SuggestFileName("x.bin"));
        }
    }
}
