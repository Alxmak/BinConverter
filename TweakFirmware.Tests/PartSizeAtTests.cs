using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Размер части по её номеру. Считает это и сама нарезка (для полосы прогресса),
    /// и предпросмотр в интерфейсе (список «Размер каждого файла»), поэтому формула одна
    /// на двоих — и проверяется здесь, а не двумя разными способами в двух местах.
    ///
    /// Главное, что проверяется, — сумма: части должны в точности складываться в исходный
    /// файл. Ошибка на последней части здесь означала бы, что человеку показали не тот
    /// размер, который получится на диске.
    /// </summary>
    public class PartSizeAtTests
    {
        [Fact]
        public void EvenSplit_AllPartsAreFull()
        {
            Assert.Equal(1024, FileSplitter.PartSizeAt(3072, 1024, 0));
            Assert.Equal(1024, FileSplitter.PartSizeAt(3072, 1024, 1));
            Assert.Equal(1024, FileSplitter.PartSizeAt(3072, 1024, 2));
        }

        [Fact]
        public void LastPart_IsTheRemainder()
        {
            Assert.Equal(1024, FileSplitter.PartSizeAt(2500, 1024, 0));
            Assert.Equal(1024, FileSplitter.PartSizeAt(2500, 1024, 1));
            Assert.Equal(452, FileSplitter.PartSizeAt(2500, 1024, 2));
        }

        [Fact]
        public void PastTheLastPart_IsZero()
        {
            Assert.Equal(0, FileSplitter.PartSizeAt(3072, 1024, 3));
            Assert.Equal(0, FileSplitter.PartSizeAt(2500, 1024, 3));
        }

        [Theory]
        [InlineData(0, 1024, 0)]     // пустой файл
        [InlineData(1024, 0, 0)]     // лимит не задан
        [InlineData(1024, -1, 0)]    // лимит отрицательный
        [InlineData(1024, 1024, -1)] // номер отрицательный
        public void NothingToMeasure_IsZero(long total, long limit, int index)
        {
            Assert.Equal(0, FileSplitter.PartSizeAt(total, limit, index));
        }

        /// <summary>
        /// Номер за пределами нарезки не должен переполнить умножение: до проверки
        /// на диапазон произведение номера на лимит выходило за long и давало
        /// отрицательный «остаток», то есть размер части меньше нуля.
        /// </summary>
        [Fact]
        public void HugeIndex_DoesNotOverflow()
        {
            long huge = 4L * 1024 * 1024 * 1024;

            Assert.Equal(0, FileSplitter.PartSizeAt(8L * 1024 * 1024 * 1024, huge, int.MaxValue));
            Assert.Equal(0, FileSplitter.PartSizeAt(long.MaxValue / 2, huge, int.MaxValue));
        }

        /// <summary>Части складываются ровно в исходный файл — ни байтом больше, ни меньше.</summary>
        [Theory]
        [InlineData(3072, 1024)]
        [InlineData(2500, 1024)]
        [InlineData(1, 1024)]
        [InlineData(8L * 1024 * 1024 * 1024, 4_055_040L * 1024)]
        public void PartsAddUpToTheWholeFile(long total, long limit)
        {
            int count = FileSplitter.CalculateExpectedPartCount(total, limit);

            long sum = 0;
            for (int i = 0; i < count; i++) sum += FileSplitter.PartSizeAt(total, limit, i);

            Assert.Equal(total, sum);
        }
    }
}
