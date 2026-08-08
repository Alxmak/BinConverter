using System.IO;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Проверки «не пишем ли поверх того, что читаем». Для сборки это вопрос сохранности
    /// данных: без этой проверки часть цепочки уничтожалась безвозвратно.
    /// </summary>
    public class OutputCollisionTests
    {
        private static readonly string[] Chain =
        {
            @"C:\dumps\emmc.bin",
            @"C:\dumps\emmc.bin.part1",
            @"C:\dumps\emmc.bin.part2"
        };

        [Fact]
        public void ChainIncludes_FindsTheBaseFile()
        {
            Assert.True(OutputCollision.ChainIncludes(Chain, @"C:\dumps\emmc.bin"));
        }

        [Fact]
        public void ChainIncludes_FindsAnyPart()
        {
            Assert.True(OutputCollision.ChainIncludes(Chain, @"C:\dumps\emmc.bin.part2"));
        }

        [Fact]
        public void ChainIncludes_IgnoresCase()
        {
            // В Windows это один и тот же файл, и перезапись уничтожила бы его так же.
            Assert.True(OutputCollision.ChainIncludes(Chain, @"c:\DUMPS\EMMC.BIN.part1"));
        }

        [Fact]
        public void ChainIncludes_SeesThroughRelativeSegments()
        {
            Assert.True(OutputCollision.ChainIncludes(Chain, @"C:\dumps\sub\..\emmc.bin"));
        }

        [Fact]
        public void ChainIncludes_LeavesADifferentNameAlone()
        {
            Assert.False(OutputCollision.ChainIncludes(Chain, @"C:\dumps\emmc_merged.bin"));
            Assert.False(OutputCollision.ChainIncludes(Chain, @"C:\other\emmc.bin"));
            // "part10" не должен считаться совпадением с "part1".
            Assert.False(OutputCollision.ChainIncludes(Chain, @"C:\dumps\emmc.bin.part10"));
        }

        [Fact]
        public void ChainIncludes_TreatsUnusableInputAsNoCollision()
        {
            Assert.False(OutputCollision.ChainIncludes(Chain, ""));
            Assert.False(OutputCollision.ChainIncludes(Chain, "   "));
        }

        // ============ Нарезка ============

        [Fact]
        public void Split_DetectsSourceEqualToTheBaseFile()
        {
            Assert.True(OutputCollision.SplitWouldOverwriteSource(
                @"C:\dumps\emmc.bin", @"C:\dumps", "emmc.bin", partCount: 4));
        }

        [Fact]
        public void Split_DetectsSourceEqualToAPartThatWouldBeWritten()
        {
            // Неочевидный случай: имена «разные», но нарезка создаёт .part1 в той же
            // папке — ровно поверх исходника.
            Assert.True(OutputCollision.SplitWouldOverwriteSource(
                @"C:\dumps\emmc.bin.part1", @"C:\dumps", "emmc.bin", partCount: 4));
        }

        [Fact]
        public void Split_IgnoresPartsBeyondWhatWillBeCreated()
        {
            // Нарезка на 2 файла создаёт emmc.bin и emmc.bin.part1 — до .part5 не дойдёт.
            Assert.False(OutputCollision.SplitWouldOverwriteSource(
                @"C:\dumps\emmc.bin.part5", @"C:\dumps", "emmc.bin", partCount: 2));
        }

        [Fact]
        public void Split_LeavesADifferentFolderAlone()
        {
            Assert.False(OutputCollision.SplitWouldOverwriteSource(
                @"C:\source\emmc.bin", @"C:\dumps", "emmc.bin", partCount: 4));
        }

        [Fact]
        public void Split_IgnoresCaseAndSeparators()
        {
            Assert.True(OutputCollision.SplitWouldOverwriteSource(
                @"c:\DUMPS\EMMC.BIN", @"C:\dumps\", "emmc.bin", partCount: 1));
        }

        [Fact]
        public void Split_TreatsUnusableInputAsNoCollision()
        {
            Assert.False(OutputCollision.SplitWouldOverwriteSource("", @"C:\dumps", "emmc.bin", 4));
            Assert.False(OutputCollision.SplitWouldOverwriteSource(@"C:\dumps\emmc.bin", "", "", 4));
        }

        [Fact]
        public void Split_WorksWithRelativeOutputFolder()
        {
            // Относительная папка приводится к полному пути от текущей — сравнение
            // должно работать и так.
            string cwd = Directory.GetCurrentDirectory();
            Assert.True(OutputCollision.SplitWouldOverwriteSource(
                Path.Combine(cwd, "emmc.bin"), ".", "emmc.bin", partCount: 1));
        }
    }
}
