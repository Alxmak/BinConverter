using System;
using System.Collections.Generic;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Analysis
{
    /// <summary>
    /// Состояние разбора одного дампа: то, что детекторы узнают друг о друге.
    ///
    /// В оригинале всё это лежало в глобальных переменных, и порядок вызовов был
    /// единственным, что удерживало их в согласии. Здесь то же самое собрано в один
    /// объект, который передаётся детекторам явно, — по нему сразу видно, какие сведения
    /// разбор накапливает по ходу и кто на них рассчитывает.
    /// </summary>
    public sealed class DumpContext
    {
        public DumpContext(IDumpReader dump)
        {
            Dump = dump ?? throw new ArgumentNullException(nameof(dump));
            LogicalSize = dump.LogicalSize;
        }

        public IDumpReader Dump { get; }

        public NandGeometry? Geometry => Dump.Geometry;

        /// <summary>Дамп страничный, со служебными областями.</summary>
        public bool IsNand => Geometry is not null;

        /// <summary>
        /// Размер данных без служебных областей. Обычно совпадает с размером дампа, но
        /// может вырасти: если таблица разделов описывает микросхему большего объёма,
        /// границы считаются от домысленного размера.
        /// </summary>
        public long LogicalSize { get; set; }

        /// <summary>Найдена ли в начале дампа таблица разделов MBR.</summary>
        public bool MbrPresent { get; set; }

        /// <summary>Номера плохих блоков микросхемы, если их таблицу удалось прочитать.</summary>
        public List<long> BadBlocks { get; } = new();

        /// <summary>Размер блока микросхемы — нужен, чтобы разложить плохие блоки по разделам.</summary>
        public long NandBlockSize { get; set; }

        /// <summary>Имя опознанной разметки — для журнала и для отчёта об итогах.</summary>
        public string? MarkName { get; set; }

        /// <summary>
        /// Нестандартная раскладка страницы Blu-ray, если детектор её вычислил. Применить
        /// её к читателю дампа должен разбор: сам детектор читателя не создаёт, а знает
        /// раскладку только он.
        /// </summary>
        public BluerayPageLayout? BluerayLayout { get; set; }

        /// <summary>
        /// Модель, версия и серийный номер, если дамп удалось опознать как прошивку
        /// Philips. По ним предлагается имя файла — переименование остаётся действием
        /// пользователя, разбор дампа сам ничего не переименовывает.
        /// </summary>
        public Partitions.Layouts.PhilipsFirmwareInfo? PhilipsInfo { get; set; }

        /// <summary>
        /// Сколько шестнадцатеричных разрядов занимает размер дампа. По этому числу
        /// выравниваются все колонки с адресами, чтобы таблица разделов читалась столбиком.
        /// </summary>
        public int HexDigits => HexWidthOf(Math.Max(LogicalSize, Dump.PhysicalSize));

        public static int HexWidthOf(long value)
        {
            int digits = 0;
            for (long v = value; v > 0; v >>= 4) digits++;
            return Math.Max(digits, 1);
        }
    }
}
