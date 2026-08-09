using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Разбор строки со списком разделов. Строки для проверки взяты из настоящих дампов —
    /// они же приведены примерами в оригинальном скрипте.
    /// </summary>
    public class MtdPartsParserTests
    {
        [Fact]
        public void Parse_MtkEmmcString()
        {
            const string Table =
                "mtdparts=mt53xx-emmc:2M(uboot),2M(uboot_env),256k(part_02),4M(kernelA)," +
                "75M(rootfsA),8M(perm),320M(3rd_ro),2048M(data)";

            var result = MtdPartsParser.Parse(Table);

            Assert.True(result.Success);
            Assert.Equal(8, result.Parts.Count);

            Assert.Equal("uboot", result.Parts[0].Name);
            Assert.Equal(0, result.Parts[0].Offset);
            Assert.Equal(2 * 1024 * 1024, result.Parts[0].Length);

            // Смещения накапливаются сами: «@» в этой строке нет ни у одной записи.
            Assert.Equal(2 * 1024 * 1024, result.Parts[1].Offset);
            Assert.Equal(4 * 1024 * 1024, result.Parts[2].Offset);
            Assert.Equal(256 * 1024, result.Parts[2].Length);

            Assert.Equal("data", result.Parts[7].Name);
            Assert.Equal(2048L * 1024 * 1024, result.Parts[7].Length);
        }

        [Fact]
        public void Parse_MstarStringWithExplicitFirstOffsetAndOpenEnd()
        {
            const string Table =
                "2560k@0x380000(MBOOT),2560k(MBOOTBAK),4m(UBILD),10m(KL),40m(CONF)," +
                "199m(UBIA),240m(UBIB),9m(tee),-(NA)";

            var result = MtdPartsParser.Parse(Table);

            Assert.Equal(9, result.Parts.Count);

            Assert.Equal("MBOOT", result.Parts[0].Name);
            Assert.Equal(0x380000, result.Parts[0].Offset);
            Assert.Equal(2560 * 1024, result.Parts[0].Length);

            // Вторая запись без «@» — продолжает первую.
            Assert.Equal(0x380000 + 2560 * 1024, result.Parts[1].Offset);

            // Последняя запись «-» означает «до конца дампа»: настоящий размер подставит
            // шаг вставки промежутков, здесь он ещё неизвестен.
            var last = result.Parts[8];
            Assert.Equal("NA", last.Name);
            Assert.True(last.HasOpenEnd);
        }

        [Fact]
        public void Parse_HexOffsetsWithOpenEndedLastPartition()
        {
            const string Table =
                "0x00002000@0x00002000(uboot),0x00006000@0x00006000(kernel)," +
                "0x00180000@0x00084000(system),-@0x00204000(userdata)";

            var result = MtdPartsParser.Parse(Table);

            Assert.Equal(4, result.Parts.Count);
            Assert.Equal(0x2000, result.Parts[0].Offset);
            Assert.Equal(0x2000, result.Parts[0].Length);
            Assert.Equal(0x84000, result.Parts[2].Offset);
            Assert.Equal(0x180000, result.Parts[2].Length);

            Assert.Equal("userdata", result.Parts[3].Name);
            Assert.Equal(0x204000, result.Parts[3].Offset);
            Assert.True(result.Parts[3].HasOpenEnd);
        }

        [Fact]
        public void Parse_KeepsTheSuffixAfterTheClosingBracketAsAComment()
        {
            // В дампах так помечают зашифрованные разделы.
            var result = MtdPartsParser.Parse("8M(perm)enc,4M(kernel)");

            Assert.Equal(2, result.Parts.Count);
            Assert.Equal("perm", result.Parts[0].Name);
            Assert.Equal("enc", result.Parts[0].Comment);
            Assert.Equal("", result.Parts[1].Comment);
        }

        [Fact]
        public void Parse_StopsWhereTheStringRunsIntoOtherData()
        {
            // После списка в дампе идут произвольные байты; пробел и знак процента —
            // признак того, что список кончился.
            var result = MtdPartsParser.Parse("2M(uboot),4M(kernel),xx %s garbage,8M(never)");

            Assert.True(result.Truncated);
            Assert.DoesNotContain(result.Parts, p => p.Name == "never");
        }

        [Fact]
        public void Parse_SingleRecordWithoutCommasIsNotAList()
        {
            // Одинокое «(что-то)» слишком легко встретить в бинарных данных случайно.
            Assert.False(MtdPartsParser.Parse("8M(perm)").Success);
        }

        [Fact]
        public void Parse_IgnoresPrefixBeforeColon()
        {
            var withPrefix = MtdPartsParser.Parse("mtdparts=nvt_nand:2M(uboot),4M(kernel)");
            var without = MtdPartsParser.Parse("2M(uboot),4M(kernel)");

            Assert.Equal(without.Parts.Count, withPrefix.Parts.Count);
            Assert.Equal(without.Parts[1].Offset, withPrefix.Parts[1].Offset);
        }

        [Fact]
        public void Parse_EmptyInputGivesNothing()
        {
            Assert.False(MtdPartsParser.Parse("").Success);
            Assert.False(MtdPartsParser.Parse("mtdparts=nand:").Success);
        }

        [Theory]
        [InlineData("2560k", 2560 * 1024L)]
        [InlineData("2560K", 2560 * 1024L)]
        [InlineData("8M", 8 * 1024 * 1024L)]
        [InlineData("8m", 8 * 1024 * 1024L)]
        [InlineData("0x380000", 0x380000L)]
        [InlineData("0X380000", 0x380000L)]
        [InlineData("-", PartitionEntry.ExtendsToEnd)]
        public void ParseSize_UnderstandsEveryFormTheOriginalDid(string text, long expected)
        {
            Assert.Equal(expected, MtdPartsParser.ParseSize(text));
        }

        [Theory]
        [InlineData("2G", 2 * 1024 * 1024 * 1024L)]
        [InlineData("2g", 2 * 1024 * 1024 * 1024L)]
        [InlineData("4096", 4096L)]
        public void ParseSize_AlsoUnderstandsGigabytesAndPlainDecimals(string text, long expected)
        {
            // Оригинал на этих записях печатал ошибку и возвращал -1, то есть превращал
            // раздел в «до конца дампа». Поддержка добавлена — ломать было нечего.
            Assert.Equal(expected, MtdPartsParser.ParseSize(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("M")]
        [InlineData("0x")]
        public void ParseSize_ReturnsNullOnAnythingThatIsNotANumber(string text)
        {
            Assert.Null(MtdPartsParser.ParseSize(text));
        }

        [Fact]
        public void ParseInto_AddsEverythingToTheTable()
        {
            var table = new PartitionTable();
            int added = MtdPartsParser.ParseInto("mtdparts=nand:2M(uboot),4M(kernel),-(data)", table);

            Assert.Equal(3, added);
            Assert.Equal(3, table.Count);
            Assert.Equal("kernel", table[1].Name);
        }
    }
}
