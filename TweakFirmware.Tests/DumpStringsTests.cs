using System.Text;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Чтение текста из двоичного блока дампа.
    ///
    /// Отсюда берутся имена разделов, а имя раздела становится именем извлечённого файла
    /// (USER_0x…_0x…_имя.bin) и попадает в .udev, по которому программатор наполняет
    /// таблицу обратно. Ошибка на единицу здесь не видна ни в интерфейсе, ни в журнале:
    /// в таблице просто будет «syste» или «system boot» вместо «system».
    ///
    /// Правило конца строки необычное и взято из оригинала: строка кончается не только
    /// нулевым байтом, но и пробелом — параметры ядра записаны в дампе одной длинной
    /// строкой через пробел.
    /// </summary>
    public class DumpStringsTests
    {
        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        [Fact]
        public void ReadUntilNulOrSpace_StopsAtASpace()
        {
            // Без остановки на пробеле в имя раздела уехал бы следующий параметр ядра.
            Assert.Equal("system", DumpStrings.ReadUntilNulOrSpace(Ascii("system boot"), 0));
        }

        [Fact]
        public void ReadUntilNulOrSpace_StopsAtANulToo()
        {
            Assert.Equal("boot", DumpStrings.ReadUntilNulOrSpace(Ascii("system\0boot\0tail"), 7));
        }

        [Fact]
        public void ReadUntilNulOrSpace_RunsToTheEndOfTheBlockWhenThereIsNoTerminator()
        {
            Assert.Equal("userdata", DumpStrings.ReadUntilNulOrSpace(Ascii("userdata"), 0));
        }

        [Fact]
        public void ReadUntilNulOrSpace_TerminatorRightAtTheOffsetGivesAnEmptyName()
        {
            Assert.Equal("", DumpStrings.ReadUntilNulOrSpace(Ascii("system boot"), 6));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(11)]
        [InlineData(100)]
        public void ReadUntilNulOrSpace_OffsetOutsideTheBlockIsNotAnError(int offset)
        {
            // Детекторы щупают адреса вслепую — выход за границу здесь штатное дело,
            // а не повод бросить исключение посреди разбора.
            Assert.Equal("", DumpStrings.ReadUntilNulOrSpace(Ascii("system boot"), offset));
        }

        [Fact]
        public void ReadFixed_StopsAtTheFirstNul()
        {
            Assert.Equal("name", DumpStrings.ReadFixed(Ascii("name\0junk"), 0, 8));
        }

        [Fact]
        public void ReadFixed_NeverReadsMoreThanAsked()
        {
            // Поле имени в таблице фиксированной длины, и следующее поле начинается
            // сразу за ним: без предела оно приехало бы в имя целиком.
            Assert.Equal("na", DumpStrings.ReadFixed(Ascii("name\0junk"), 0, 2));
        }

        [Fact]
        public void ReadFixed_StopsAtTheEndOfTheBlock()
        {
            Assert.Equal("cd", DumpStrings.ReadFixed(Ascii("abcd"), 2, 100));
        }

        [Fact]
        public void ReadFixed_KeepsSpacesUnlikeTheOtherReader()
        {
            // Здесь пробел — часть имени: у полей фиксированной длины конец задан длиной,
            // а не разделителем. Две функции различаются именно этим.
            Assert.Equal("two words", DumpStrings.ReadFixed(Ascii("two words\0"), 0, 16));
        }

        [Fact]
        public void IndexOf_FindsTheSignature()
        {
            Assert.Equal(2, DumpStrings.IndexOf(Ascii("xxMAGICxx"), "MAGIC"));
        }

        [Fact]
        public void IndexOf_AnswersMinusOneWhenThereIsNothing()
        {
            Assert.Equal(-1, DumpStrings.IndexOf(Ascii("xxMAGICxx"), "NOPE"));
        }

        [Fact]
        public void IndexOf_TheSignatureMustFitEntirelyInsideTheLimit()
        {
            byte[] block = Ascii("xxMAGICxx");

            // «MAGIC» занимает байты 2..6. Предел в семь байт вмещает его целиком,
            // предел в шесть — нет, и наполовину совпавшая сигнатура сигнатурой не считается.
            Assert.Equal(2, DumpStrings.IndexOf(block, "MAGIC", 7));
            Assert.Equal(-1, DumpStrings.IndexOf(block, "MAGIC", 6));
        }

        [Fact]
        public void IndexOf_EmptyNeedleIsNotFoundAnywhere()
        {
            // Иначе «нашли по адресу 0» на любом блоке.
            Assert.Equal(-1, DumpStrings.IndexOf(Ascii("anything"), ""));
        }
    }
}
