using System;

namespace TweakFirmware.Core.Dump
{
    /// <summary>Как служебные байты spare размещены на странице NAND.</summary>
    public enum NandPageLayout
    {
        /// <summary>Spare нет: дамп eMMC/SD — сплошной массив байт.</summary>
        None,

        /// <summary>Обычная схема: страница = [main][spare], и так до конца дампа.</summary>
        Standard,

        /// <summary>
        /// Плееры Dune HD. Внутри одной физической страницы 0x840 лежат метка, четыре
        /// куска данных разной длины, три вставки spare по 13 байт и восемь байт мусора.
        /// Раскладка жёстко зашита — вычислить её из main/spare нельзя.
        /// </summary>
        DuneHd
    }

    /// <summary>Какой именно Dune HD опознан по метке в начале страницы.</summary>
    public enum DuneHdVariant
    {
        None,

        /// <summary>Метка FF FF AA FF.</summary>
        Tv102,

        /// <summary>Метка AA AA AA AA.</summary>
        Tv301
    }

    /// <summary>
    /// Геометрия страницы NAND и перевод адресов между двумя системами координат.
    ///
    /// Это ключевое понятие всего разбора разделов. В дампе NAND за каждыми
    /// <see cref="Main"/> байтами данных идут <see cref="Spare"/> служебных байт (ECC,
    /// маркер плохого блока), и таблицы разделов внутри прошивки адресуют данные так,
    /// будто spare не существует. Поэтому весь код разбора работает в «логических»
    /// адресах (только main), а в «физические» (как в файле) они переводятся ровно в трёх
    /// местах: показ таблицы, запись .udev со spare и извлечение разделов.
    ///
    /// Для eMMC геометрии нет вовсе — там обе системы координат совпадают, и признаком
    /// этого служит <c>Geometry == null</c> у читателя дампа.
    /// </summary>
    public sealed record NandGeometry
    {
        public NandGeometry(int main, int spare, int relativeMain, int relativeSpare,
                            NandPageLayout layout = NandPageLayout.Standard,
                            DuneHdVariant duneVariant = DuneHdVariant.None)
        {
            if (main <= 0) throw new ArgumentOutOfRangeException(nameof(main));
            if (spare < 0) throw new ArgumentOutOfRangeException(nameof(spare));

            Main = main;
            Spare = spare;
            RelativeMain = relativeMain;
            RelativeSpare = relativeSpare;
            Layout = layout;
            DuneVariant = duneVariant;
        }

        /// <summary>Байт данных на странице.</summary>
        public int Main { get; }

        /// <summary>Служебных байт на странице.</summary>
        public int Spare { get; }

        /// <summary>Полный размер страницы в файле.</summary>
        public int Page => Main + Spare;

        /// <summary>
        /// Соотношение main:spare, выведенное из размера файла. Хранится потому, что по нему
        /// восстанавливается spare при ручном выборе размера страницы пользователем.
        /// </summary>
        public int RelativeMain { get; }

        /// <inheritdoc cref="RelativeMain"/>
        public int RelativeSpare { get; }

        public NandPageLayout Layout { get; init; }

        public DuneHdVariant DuneVariant { get; init; }

        /// <summary>
        /// Логический адрес → физический (вставляет пропуски под spare).
        /// Отрицательные значения передавать нельзя: длина -1 («до конца дампа») должна
        /// быть развёрнута в настоящий размер раньше, на шаге вставки промежутков.
        /// </summary>
        public long AddSpare(long logical)
        {
            if (logical < 0) throw new ArgumentOutOfRangeException(nameof(logical));
            return logical / Main * Page + logical % Main;
        }

        /// <summary>Физический адрес → логический (вычитает spare).</summary>
        public long MinusSpare(long physical)
        {
            if (physical < 0) throw new ArgumentOutOfRangeException(nameof(physical));
            return physical / Page * Main + physical % Page;
        }
    }

    /// <summary>
    /// Один вариант геометрии для ручного выбора, когда автоопределение не справилось.
    /// Показывается пользователю списком; <see cref="Index"/> — то, что он выбирает.
    /// </summary>
    public readonly record struct NandGeometryOption(int Index, int Main, int Spare)
    {
        /// <summary>Особый вариант «spare нет, считать дамп сплошным».</summary>
        public bool IsNoSpare => Main == 0;
    }
}
