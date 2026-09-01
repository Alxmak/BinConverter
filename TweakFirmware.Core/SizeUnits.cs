namespace TweakFirmware.Core
{
    /// <summary>Единица, в которой человек задаёт размер.</summary>
    public enum SizeUnit
    {
        Bytes,
        Kilobytes,
        Megabytes,
        Gigabytes
    }

    /// <summary>
    /// Перевод размера между байтами и удобными единицами.
    ///
    /// Лимит на один файл до сих пор задавался только в байтах: чтобы поставить
    /// «два гигабайта», нужно было набрать 2147483648 — и не ошибиться ни в одной
    /// из десяти цифр, потому что ошибка на разряд здесь означает вдесятеро больше
    /// файлов или один файл вместо десяти, и замечается она уже по готовой нарезке.
    ///
    /// Кратность 1024, как и у <see cref="SizeFormatHelper"/>: программа везде считает
    /// «килобайт» равным 1024 байтам, и единица, означающая в одном поле одно, а в
    /// соседней строке другое, — худшее, что можно тут сделать.
    /// </summary>
    public static class SizeUnits
    {
        public const long Kilobyte = 1024L;
        public const long Megabyte = 1024L * 1024;
        public const long Gigabyte = 1024L * 1024 * 1024;

        /// <summary>Сколько байт в одной такой единице.</summary>
        public static long Multiplier(SizeUnit unit) => unit switch
        {
            SizeUnit.Kilobytes => Kilobyte,
            SizeUnit.Megabytes => Megabyte,
            SizeUnit.Gigabytes => Gigabyte,
            _ => 1L
        };

        /// <summary>
        /// Самая крупная единица, в которой этот размер выражается целым числом.
        ///
        /// Дробей в поле нет намеренно: «1.5 ГБ» пришлось бы разбирать с оглядкой
        /// на разделитель дробной части, который в русской раскладке запятая, а в
        /// английской точка, — и на пол-байта, которых не бывает. Целое число
        /// в подобранной единице говорит то же самое и разбирается однозначно.
        /// </summary>
        public static SizeUnit Best(long bytes)
        {
            if (bytes <= 0) return SizeUnit.Bytes;

            if (bytes % Gigabyte == 0) return SizeUnit.Gigabytes;
            if (bytes % Megabyte == 0) return SizeUnit.Megabytes;
            if (bytes % Kilobyte == 0) return SizeUnit.Kilobytes;

            return SizeUnit.Bytes;
        }

        /// <summary>
        /// Размер в байтах или 0, если число в такой единице в long не помещается.
        /// Ноль здесь означает «лимит не задан» — ровно как у остальных негодных
        /// значений в <see cref="PartSizeLimit"/>.
        /// </summary>
        public static long ToBytes(long value, SizeUnit unit)
        {
            if (value <= 0) return 0;

            long multiplier = Multiplier(unit);
            return value > long.MaxValue / multiplier ? 0 : value * multiplier;
        }

        /// <summary>
        /// Размер в выбранной единице, целым. Остаток отбрасывается — вызывать это
        /// имеет смысл с единицей от <see cref="Best"/>, там остатка не бывает.
        /// </summary>
        public static long ToUnit(long bytes, SizeUnit unit) => bytes / Multiplier(unit);
    }
}
