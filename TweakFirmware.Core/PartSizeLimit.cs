using System.Text;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Разбор лимита на один файл. Значение приходит из двух источников: у готовых пресетов
    /// программаторов оно фиксировано, у пользовательского — вводится руками, причём поле
    /// показывает разряды с разделителями ("500 000 000"), поэтому перед разбором из строки
    /// нужно оставить только цифры.
    ///
    /// От этого числа напрямую зависит, на сколько частей разрежется прошивка и какого они
    /// будут размера, — то есть основной смысл программы. Логика вынесена сюда, чтобы
    /// граничные случаи (пустая строка, ноль, мусор, значение с разделителями) проверялись
    /// тестами, а не на живой прошивке в несколько гигабайт.
    /// </summary>
    public static class PartSizeLimit
    {
        /// <summary>Оставляет в строке только цифры — разделители разрядов и пробелы отбрасываются.</summary>
        public static string DigitsOnly(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (char.IsDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// Итоговый лимит в байтах. У пресета с фиксированным значением оно имеет приоритет
        /// и текст поля не разбирается вовсе. Возвращает 0, если значение задать не удалось, —
        /// вызывающий код трактует 0 как "лимит не задан" и не строит предпросмотр.
        /// </summary>
        public static long Resolve(long? presetLimitBytes, string? customLimitText) =>
            Resolve(presetLimitBytes, customLimitText, SizeUnit.Bytes);

        /// <summary>
        /// То же, но число в поле задано в выбранной единице, а не обязательно в байтах.
        ///
        /// Отдельная перегрузка, а не замена: единицы появились у лимита позже самого
        /// лимита, и «в байтах» осталось обычным случаем — как для старого вызова,
        /// так и для проверок, которым единица безразлична.
        /// </summary>
        public static long Resolve(long? presetLimitBytes, string? customLimitText, SizeUnit unit)
        {
            if (presetLimitBytes.HasValue) return presetLimitBytes.Value;

            return long.TryParse(DigitsOnly(customLimitText), out long value) && value > 0
                ? SizeUnits.ToBytes(value, unit)
                : 0;
        }
    }
}
