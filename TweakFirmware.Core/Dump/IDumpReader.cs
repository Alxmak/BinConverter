using System;

namespace TweakFirmware.Core.Dump
{
    /// <summary>
    /// Дамп прошивки как сплошной массив байт.
    ///
    /// Смысл этой абстракции в том, что вызывающий код не знает и не хочет знать, лежит
    /// перед ним eMMC или NAND со служебными вставками spare: он просит байты по
    /// логическому адресу, а пропуски служебных областей — забота реализации. Именно так
    /// устроен и оригинальный скрипт: там всё чтение шло через одну функцию loadData(),
    /// и все два с лишним десятка детекторов разметок написаны так, будто дамп ровный.
    /// </summary>
    public interface IDumpReader : IDisposable
    {
        /// <summary>Размер файла на диске.</summary>
        long PhysicalSize { get; }

        /// <summary>
        /// Размер данных без служебных байт. Для eMMC совпадает с <see cref="PhysicalSize"/>.
        /// Именно от него считаются границы разделов и хвостовой промежуток.
        /// </summary>
        long LogicalSize { get; }

        /// <summary>Геометрия страницы или <c>null</c>, если дамп сплошной (eMMC/SD).</summary>
        NandGeometry? Geometry { get; }

        /// <summary>
        /// Читает данные по логическому адресу. Возвращает, сколько байт удалось прочитать:
        /// у конца дампа это может быть меньше запрошенного, остаток буфера обнуляется.
        /// Выхода за границу не считаем ошибкой — детекторы штатно щупают адреса вслепую.
        /// </summary>
        int Read(long logicalOffset, Span<byte> destination);

        /// <summary>
        /// Удобная обёртка: выделяет массив нужной длины и читает в него. Массив всегда
        /// возвращается запрошенного размера, недочитанный хвост заполнен нулями.
        /// </summary>
        byte[] ReadBlock(long logicalOffset, int length);
    }

    /// <summary>Общие мелочи для реализаций <see cref="IDumpReader"/>.</summary>
    public static class DumpReaderExtensions
    {
        /// <summary>
        /// Есть ли в дампе указанный логический диапазон целиком. Детекторы вызывают это
        /// перед чтением таблицы по фиксированному адресу, чтобы не тратить время на дамп,
        /// который заведомо меньше.
        /// </summary>
        public static bool Contains(this IDumpReader dump, long logicalOffset, long length) =>
            logicalOffset >= 0 && length >= 0 && logicalOffset + length <= dump.LogicalSize;
    }
}
