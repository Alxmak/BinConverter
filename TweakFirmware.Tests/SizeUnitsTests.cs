using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Единицы у лимита на файл. От этого числа зависит, на сколько частей разрежется
    /// прошивка, — то есть основной смысл программы, — и ошибка на множитель здесь
    /// замечается уже по готовой нарезке.
    /// </summary>
    public class SizeUnitsTests
    {
        [Fact]
        public void KilobyteIs1024_NotAThousand()
        {
            // Программа везде считает килобайт равным 1024 байтам (SizeFormatHelper),
            // и единица, означающая в одном поле одно, а в соседней строке другое, —
            // худшее, что тут можно сделать.
            Assert.Equal(1024L, SizeUnits.Multiplier(SizeUnit.Kilobytes));
            Assert.Equal(1024L * 1024, SizeUnits.Multiplier(SizeUnit.Megabytes));
            Assert.Equal(1024L * 1024 * 1024, SizeUnits.Multiplier(SizeUnit.Gigabytes));
            Assert.Equal(1L, SizeUnits.Multiplier(SizeUnit.Bytes));
        }

        [Fact]
        public void PresetLimit_ShowsUpAsWholeMegabytes()
        {
            // Ради этого единицы и заводились: 4 152 360 960 в поле не запоминается
            // и не пересказывается, а «3960 МБ» — вполне.
            long limit = FileSplitter.DefaultMaxPartSizeBytes;

            Assert.Equal(SizeUnit.Megabytes, SizeUnits.Best(limit));
            Assert.Equal(3960L, SizeUnits.ToUnit(limit, SizeUnit.Megabytes));
        }

        [Theory]
        [InlineData(1024L * 1024 * 1024, SizeUnit.Gigabytes)]
        [InlineData(3L * 1024 * 1024 * 1024, SizeUnit.Gigabytes)]
        [InlineData(1024L * 1024, SizeUnit.Megabytes)]
        [InlineData(1536L * 1024, SizeUnit.Kilobytes)]
        [InlineData(1024L, SizeUnit.Kilobytes)]
        [InlineData(1023L, SizeUnit.Bytes)]
        [InlineData(1000L, SizeUnit.Bytes)]
        public void Best_PicksTheLargestUnitThatStaysWhole(long bytes, SizeUnit expected)
        {
            Assert.Equal(expected, SizeUnits.Best(bytes));
        }

        [Fact]
        public void Best_OfNothing_IsBytes()
        {
            // Пустое поле и ноль — не повод показывать «0 ГБ».
            Assert.Equal(SizeUnit.Bytes, SizeUnits.Best(0));
            Assert.Equal(SizeUnit.Bytes, SizeUnits.Best(-1));
        }

        [Fact]
        public void ToBytes_MultipliesByTheUnit()
        {
            Assert.Equal(2L * 1024 * 1024 * 1024, SizeUnits.ToBytes(2, SizeUnit.Gigabytes));
            Assert.Equal(500L * 1024 * 1024, SizeUnits.ToBytes(500, SizeUnit.Megabytes));
            Assert.Equal(777L, SizeUnits.ToBytes(777, SizeUnit.Bytes));
        }

        [Fact]
        public void ToBytes_OfAnAbsurdNumber_MeansNoLimitAtAll()
        {
            // Набрать в поле двадцать цифр никто не мешает. Переполнение long дало бы
            // отрицательный лимит, а с ним — «файлов: 0» и нарезку, которой не будет.
            Assert.Equal(0L, SizeUnits.ToBytes(long.MaxValue, SizeUnit.Gigabytes));
            Assert.Equal(0L, SizeUnits.ToBytes(0, SizeUnit.Megabytes));
            Assert.Equal(0L, SizeUnits.ToBytes(-5, SizeUnit.Megabytes));
        }

        [Fact]
        public void Limit_IsResolvedInTheChosenUnit()
        {
            Assert.Equal(2L * 1024 * 1024 * 1024, PartSizeLimit.Resolve(null, "2", SizeUnit.Gigabytes));

            // Поле показывает число с разделителями разрядов — они в разбор не идут.
            Assert.Equal(3960L * 1024 * 1024, PartSizeLimit.Resolve(null, "3 960", SizeUnit.Megabytes));
        }

        [Fact]
        public void FixedPreset_IgnoresBothTheFieldAndTheUnit()
        {
            // У готового программатора лимит его собственный: что бы ни стояло в поле,
            // выбранная модель важнее.
            Assert.Equal(FileSplitter.DefaultMaxPartSizeBytes,
                PartSizeLimit.Resolve(FileSplitter.DefaultMaxPartSizeBytes, "7", SizeUnit.Gigabytes));
        }

        [Fact]
        public void OldTwoArgumentForm_StillMeansBytes()
        {
            // Перегрузка без единицы осталась ради вызовов, которым единица безразлична.
            Assert.Equal(500L, PartSizeLimit.Resolve(null, "500"));
        }
    }
}
