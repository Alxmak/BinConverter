using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Округление размера тома и обрезка его по границе раздела.
    ///
    /// Через эти две мелочи проходит окончательная длина каждого найденного тома, а длина
    /// тома — это длина извлечённого файла. Заголовок в дампе может врать (повреждённая
    /// прошивка — обычное дело), и обрезка как раз для этого: без неё программа читала бы
    /// за границей раздела и складывала бы в файл чужие данные.
    /// </summary>
    public class ProbeHelpersTests
    {
        private static VolumeInfo Volume(long offset, long length) =>
            new(FsType.Ext4, "system", offset, length);

        [Fact]
        public void BlockAlignment_IsFourKilobytes()
        {
            // На это число опираются все зонды. Оно из оригинала: образы, которые пишутся
            // целиком, занимают на носителе целое число блоков.
            Assert.Equal(0x1000, ProbeHelpers.BlockAlignment);
        }

        [Theory]
        [InlineData(0x1000, 0x1000, 0x1000)]   // ровно по границе — не трогаем
        [InlineData(0x1001, 0x1000, 0x2000)]   // на байт больше — целый блок вверх
        [InlineData(0x0FFF, 0x1000, 0x1000)]
        [InlineData(0, 0x1000, 0)]             // ноль остаётся нулём, а не превращается в блок
        public void AlignUp_RoundsUpToWholeBlocks(long value, long alignment, long expected)
        {
            Assert.Equal(expected, ProbeHelpers.AlignUp(value, alignment));
        }

        [Fact]
        public void AlignUp_MeaninglessAlignmentLeavesTheValueAlone()
        {
            // Ноль пришёл бы делением на ноль, отрицательное — бессмыслицей.
            // Обе ветки должны молча вернуть исходное, а не уронить разбор.
            Assert.Equal(0x1234, ProbeHelpers.AlignUp(0x1234, 0));
            Assert.Equal(0x1234, ProbeHelpers.AlignUp(0x1234, -1));
        }

        [Fact]
        public void ClampToPartition_VolumeThatFitsIsLeftAsIs()
        {
            var volume = Volume(0x1000, 0x1000);

            var result = ProbeHelpers.ClampToPartition(volume, 0x4000);

            Assert.Equal(0x1000, result.Length);
            Assert.Equal("", result.Comment);
        }

        [Fact]
        public void ClampToPartition_VolumeEndingExactlyAtTheBorderIsNotClamped()
        {
            var result = ProbeHelpers.ClampToPartition(Volume(0x1000, 0x1000), 0x2000);

            Assert.Equal(0x1000, result.Length);
            Assert.Equal("", result.Comment);
        }

        [Fact]
        public void ClampToPartition_VolumeStickingOutIsCutAndExplained()
        {
            // Заголовок обещает том до 0x3000, а раздел кончается на 0x1800.
            var result = ProbeHelpers.ClampToPartition(Volume(0x1000, 0x2000), 0x1800);

            Assert.Equal(0x800, result.Length);

            // Молча обрезать нельзя: укороченный том выглядит как целый, и понять,
            // почему файл получился меньше, будет неоткуда.
            Assert.NotEqual("", result.Comment);
        }

        [Fact]
        public void ClampToPartition_UnknownBorderIsNotABorder()
        {
            // Ноль означает «границы не знаем» — сканируется весь дамп целиком.
            var result = ProbeHelpers.ClampToPartition(Volume(0x1000, 0x2000), 0);

            Assert.Equal(0x2000, result.Length);
            Assert.Equal("", result.Comment);
        }

        [Fact]
        public void ClampToPartition_VolumeStartingPastTheBorderIsLeftAlone()
        {
            // Обрезка дала бы отрицательную длину. Такой том — это ошибка вызывающего
            // кода, а не повод записать в таблицу запись с длиной меньше нуля.
            var result = ProbeHelpers.ClampToPartition(Volume(0x5000, 0x100), 0x4000);

            Assert.Equal(0x100, result.Length);
            Assert.Equal("", result.Comment);
        }

        [Fact]
        public void ClampToPartition_KeepsEverythingElseAboutTheVolume()
        {
            var volume = Volume(0x1000, 0x2000) with { Details = new[] { "строка журнала" } };

            var result = ProbeHelpers.ClampToPartition(volume, 0x1800);

            Assert.Equal(FsType.Ext4, result.Type);
            Assert.Equal("system", result.Name);
            Assert.Equal(0x1000, result.Offset);
            Assert.Single(result.Details);
        }
    }
}
