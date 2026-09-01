using System;
using System.IO;
using System.Text;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Быстрые проверки «лежит ли здесь начало такого-то тома» и контрольная сумма блоков.
    ///
    /// Ими разбор ищет опорные точки там, где таблицы разделов нет вовсе: у Sony границы
    /// восстанавливаются по тому, где в дампе встречаются суперблоки. Сигнатура и её
    /// смещение — это соглашение формата, а не наш выбор: смещение, сдвинутое на правке,
    /// не сломает ни сборку, ни один существующий тест, просто разделы перестанут
    /// находиться на настоящих дампах, проверить которые нечем.
    /// </summary>
    public class SignatureTests
    {
        private static IDumpReader DumpOf(DumpBuilder builder) =>
            new PlainDumpReader(new MemoryStream(builder.Build()));

        // ---------- ext4 ----------

        [Fact]
        public void IsExt4_FindsTheMagicInTheSuperblock()
        {
            // Суперблок лежит со смещением 0x400 от начала тома, магическое число —
            // ещё через 0x38.
            var builder = new DumpBuilder(0x1000);
            builder.WriteUInt16(QuickSignatures.Ext4SuperblockOffset + 0x38, 0xEF53);

            using var dump = DumpOf(builder);
            Assert.True(QuickSignatures.IsExt4(dump, 0));
        }

        [Fact]
        public void IsExt4_MagicOneValueOffIsNotExt4()
        {
            var builder = new DumpBuilder(0x1000);
            builder.WriteUInt16(QuickSignatures.Ext4SuperblockOffset + 0x38, 0xEF52);

            using var dump = DumpOf(builder);
            Assert.False(QuickSignatures.IsExt4(dump, 0));
        }

        [Fact]
        public void IsExt4_MagicAtAnotherPlaceIsNotExt4()
        {
            // Смещение — часть договора: то же число, лежащее не там, ничего не значит.
            var builder = new DumpBuilder(0x1000);
            builder.WriteUInt16(QuickSignatures.Ext4SuperblockOffset, 0xEF53);

            using var dump = DumpOf(builder);
            Assert.False(QuickSignatures.IsExt4(dump, 0));
        }

        [Fact]
        public void IsExt4_TooCloseToTheEndOfTheDumpIsRefusedWithoutReading()
        {
            // Дампа не хватает даже на суперблок — читать нечего, и это не ошибка.
            var builder = new DumpBuilder(0x800);
            builder.WriteUInt16(QuickSignatures.Ext4SuperblockOffset + 0x38, 0xEF53);

            using var dump = DumpOf(builder);
            Assert.False(QuickSignatures.IsExt4(dump, 0));
        }

        // ---------- FAT16 ----------

        [Theory]
        [InlineData(0x36)]
        [InlineData(0x52)]
        public void IsFat16_LabelLivesInOneOfTwoPlaces(int labelOffset)
        {
            // Где именно — зависит от того, каким инструментом том создавали.
            var builder = new DumpBuilder(0x1000);
            builder.WriteAscii(labelOffset, "FAT16");

            using var dump = DumpOf(builder);
            Assert.True(QuickSignatures.IsFat16(dump, 0));
        }

        [Fact]
        public void IsFat16_LabelSomewhereElseDoesNotCount()
        {
            var builder = new DumpBuilder(0x1000);
            builder.WriteAscii(0x40, "FAT16");

            using var dump = DumpOf(builder);
            Assert.False(QuickSignatures.IsFat16(dump, 0));
        }

        [Fact]
        public void IsFat16_Fat32IsNotFat16()
        {
            var builder = new DumpBuilder(0x1000);
            builder.WriteAscii(0x52, "FAT32");

            using var dump = DumpOf(builder);
            Assert.False(QuickSignatures.IsFat16(dump, 0));
        }

        // ---------- AVB ----------

        [Fact]
        public void IsAvb_RecognisesTheHeader()
        {
            var builder = new DumpBuilder(0x1000);
            builder.WriteAscii(0, "AVB0");

            using var dump = DumpOf(builder);
            Assert.True(QuickSignatures.IsAvb(dump, 0));
        }

        [Fact]
        public void IsAvb_HeaderMustStartExactlyAtTheOffsetAsked()
        {
            var builder = new DumpBuilder(0x1000);
            builder.WriteAscii(1, "AVB0");

            using var dump = DumpOf(builder);
            Assert.False(QuickSignatures.IsAvb(dump, 0));
        }

        // ---------- CRC32 ----------

        [Fact]
        public void Crc32_MatchesTheStandardCheckValue()
        {
            // Контрольное значение из спецификации CRC-32/ISO-HDLC: строка «123456789»
            // даёт 0xCBF43926. Если однажды поменяются полином, начальное значение или
            // завершающая инверсия, сойдётся с этим числом только правильный вариант.
            Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        }

        [Fact]
        public void CheckLeadingChecksum_AcceptsABlockWhoseSumMatches()
        {
            byte[] block = BlockWithChecksum(Encoding.ASCII.GetBytes("partition table payload"));

            Assert.True(Crc32.CheckLeadingChecksum(block));
        }

        [Fact]
        public void CheckLeadingChecksum_RejectsABlockWithASingleChangedByte()
        {
            // Ради этого проверка и нужна: половина детекторов ищет свою таблицу не по
            // сигнатуре, а по сходящейся сумме — случайные данные её не проходят.
            byte[] block = BlockWithChecksum(Encoding.ASCII.GetBytes("partition table payload"));
            block[^1] ^= 0x01;

            Assert.False(Crc32.CheckLeadingChecksum(block));
        }

        [Fact]
        public void CheckLeadingChecksum_BlockWithoutPayloadIsNotAcceptable()
        {
            // В блоке из одной суммы проверять нечего, и «сошлось» здесь означало бы
            // опознанную таблицу на пустом месте.
            Assert.False(Crc32.CheckLeadingChecksum(new byte[4]));
            Assert.False(Crc32.CheckLeadingChecksum(Array.Empty<byte>()));
        }

        /// <summary>Блок в том виде, в каком он лежит в дампе: сумма, а за ней данные.</summary>
        private static byte[] BlockWithChecksum(byte[] payload)
        {
            var block = new byte[4 + payload.Length];
            payload.CopyTo(block, 4);

            uint crc = Crc32.Compute(payload);
            block[0] = (byte)crc;
            block[1] = (byte)(crc >> 8);
            block[2] = (byte)(crc >> 16);
            block[3] = (byte)(crc >> 24);

            return block;
        }
    }
}
