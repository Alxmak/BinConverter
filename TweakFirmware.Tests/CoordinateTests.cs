using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Перевод адресов из логических в физические и ширина адреса в шестнадцатеричном виде.
    ///
    /// Внутри разбора всё считается в логических координатах — без служебных областей
    /// spare. Физические появляются ровно на трёх границах: печать таблицы, второй файл
    /// .udev и извлечение файлов. Ошибка на этой границе не видна ни в интерфейсе,
    /// ни в журнале: таблица выглядит правдоподобно, а программатор пишет по сдвинутым
    /// адресам — то есть портит микросхему.
    /// </summary>
    public class CoordinateTests
    {
        /// <summary>Обычная геометрия: 2048 байт данных и 64 служебных на страницу.</summary>
        private static NandGeometry Geometry() => new(2048, 64, 0x20, 0x01);

        [Fact]
        public void ToPhysical_ShiftsBothOffsetAndLength()
        {
            var geometry = Geometry();
            var part = new PartitionEntry { Name = "system", Offset = 2048 * 10, Length = 2048 * 4 };

            var physical = part.ToPhysical(geometry);

            // Десять страниц данных лежат в дампе как десять полных страниц со spare.
            Assert.Equal(2112 * 10, physical.Offset);
            Assert.Equal(2112 * 4, physical.Length);

            // Длину надо пересчитывать тоже, а не только начало: раздел на четыре страницы
            // занимает в дампе больше места, чем его полезные байты.
            Assert.NotEqual(part.Length, physical.Length);
        }

        [Fact]
        public void ToPhysical_KeepsTheRemainderInsideThePage()
        {
            var geometry = Geometry();
            var part = new PartitionEntry { Offset = 2048 + 100, Length = 2048 + 7 };

            var physical = part.ToPhysical(geometry);

            // Целые страницы разворачиваются со spare, остаток — как есть: он лежит
            // внутри страницы, до её служебной области.
            Assert.Equal(2112 + 100, physical.Offset);
            Assert.Equal(2112 + 7, physical.Length);
        }

        [Fact]
        public void ToPhysical_OpenEndedLengthIsLeftAlone()
        {
            // «До конца дампа» разворачивается раньше, на шаге вставки промежутков.
            // Если такая длина всё же дошла сюда, пересчитывать нечего — и, что важнее,
            // AddSpare на отрицательном числе бросает исключение, то есть без этой
            // проверки разбор падал бы вместо того, чтобы записать .udev.
            var part = new PartitionEntry { Offset = 0, Length = PartitionEntry.ExtendsToEnd };

            var physical = part.ToPhysical(Geometry());

            Assert.Equal(PartitionEntry.ExtendsToEnd, physical.Length);
        }

        [Fact]
        public void ToPhysical_ZeroLengthStaysZero()
        {
            var part = new PartitionEntry { Offset = 2048, Length = 0 };

            Assert.Equal(0, part.ToPhysical(Geometry()).Length);
        }

        [Fact]
        public void ToPhysical_DoesNotTouchTheOriginal()
        {
            // Пересчёт делается копией намеренно: таблица на экране и таблица в памяти
            // остаются логическими, иначе переключатель «показывать физические адреса»
            // после первого нажатия сдвигал бы их навсегда.
            var part = new PartitionEntry { Offset = 2048 * 3, Length = 2048 };

            part.ToPhysical(Geometry());

            Assert.Equal(2048 * 3, part.Offset);
            Assert.Equal(2048, part.Length);
        }

        [Theory]
        [InlineData(0, 1)]            // ноль — всё равно одна цифра, а не пустое место
        [InlineData(0xF, 1)]
        [InlineData(0x10, 2)]
        [InlineData(0xFFFF, 4)]
        [InlineData(0x1_0000_0000, 9)]
        public void HexWidthOf_CountsDigitsOfTheLargestAddress(long value, int expected)
        {
            Assert.Equal(expected, DumpContext.HexWidthOf(value));
        }

        [Fact]
        public void HexWidthOf_NegativeValueStillGivesAWidth()
        {
            // Отрицательного размера дампа быть не может, но ширина в ноль знаков
            // сломала бы форматирование всей таблицы, а не одну строку.
            Assert.Equal(1, DumpContext.HexWidthOf(-1));
        }
    }
}
