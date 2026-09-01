using System.IO;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Подсчёт места под результат.
    ///
    /// Этой проверкой операция решает, начинать ли работу вообще: не хватило места —
    /// не начинаем, потому что оборвавшаяся на середине запись оставляет недописанные
    /// файлы, которые выглядят как готовые. Запас в пять процентов — на округление
    /// по кластерам файловой системы: файл на диске занимает целое число кластеров,
    /// и сумма размеров всегда чуть меньше того, что уйдёт на самом деле.
    /// </summary>
    public class DiskSpaceTests
    {
        [Fact]
        public void EnoughSpace_WhenAvailableIsNotLessThanRequired()
        {
            var exact = new SpaceCheckResult { RequiredBytes = 1000, AvailableBytes = 1000 };

            // Ровно столько же — это «хватает»: иначе операция отказывалась бы работать
            // на полностью подходящем диске.
            Assert.True(exact.HasEnoughSpace);
            Assert.Equal(0, exact.MissingBytes);
        }

        [Fact]
        public void NotEnoughSpace_TellsHowMuchIsMissing()
        {
            var tight = new SpaceCheckResult { RequiredBytes = 1000, AvailableBytes = 900 };

            Assert.False(tight.HasEnoughSpace);

            // Число уходит в сообщение пользователю — «не хватает 100 байт»,
            // а не просто «места мало».
            Assert.Equal(100, tight.MissingBytes);
        }

        [Fact]
        public void MissingBytes_IsZeroWheneverSpaceIsEnough()
        {
            var plenty = new SpaceCheckResult { RequiredBytes = 1000, AvailableBytes = 5000 };

            Assert.Equal(0, plenty.MissingBytes);
        }

        [Fact]
        public void CheckSpace_AddsTheFivePercentMargin()
        {
            var check = DiskSpaceHelper.CheckSpace(Path.GetTempPath(), 1000);

            Assert.Equal(1050, check.RequiredBytes);
        }

        [Fact]
        public void CheckSpace_MarginCanBeTurnedOff()
        {
            var check = DiskSpaceHelper.CheckSpace(Path.GetTempPath(), 1000, marginFraction: 0);

            Assert.Equal(1000, check.RequiredBytes);
        }

        [Fact]
        public void CheckSpace_AsksTheRealDriveAndAnswersWithoutThrowing()
        {
            // Нулевой размер проходит на любом диске: смысл проверки в том, что путь
            // к настоящей папке разбирается и свободное место у системы спрашивается.
            var check = DiskSpaceHelper.CheckSpace(Path.GetTempPath(), 0);

            Assert.True(check.HasEnoughSpace);
            Assert.True(check.AvailableBytes >= 0);
        }
    }
}
