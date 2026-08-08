namespace TweakFirmware.Core
{
    /// <summary>
    /// Разбор SHA-256, введённого человеком. Нужен потому, что поле «Ожидаемый SHA-256»
    /// в Сборке заполняется вставкой из буфера, а там бывает всё: лишние пробелы по краям,
    /// перевод строки в середине (в том числе от нашего же окна с итогом — там хэш
    /// разбит на строки, см. <see cref="HashDisplay"/>), обрезанная копия.
    ///
    /// Без этой проверки опечатка доезжала до конца сборки и выдавала «Ошибка проверки»
    /// с текстом про повреждённые данные — то есть программа обвиняла файл в том, что
    /// человек неудачно вставил строку.
    /// </summary>
    public static class Sha256Text
    {
        /// <summary>Длина SHA-256 в шестнадцатеричной записи.</summary>
        public const int HexLength = 64;

        /// <summary>
        /// Убирает любые пробельные символы — вставленный хэш становится одной строкой.
        /// Регистр не меняем: сравнение всё равно идёт без учёта регистра, а показывать
        /// человеку лучше то, что он ввёл.
        /// </summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var chars = new char[text.Length];
            int count = 0;
            foreach (char c in text)
                if (!char.IsWhiteSpace(c)) chars[count++] = c;

            return new string(chars, 0, count);
        }

        /// <summary>Пусто ли поле по существу: только пробелы считаются незаполненным.</summary>
        public static bool IsBlank(string? text) => Normalize(text).Length == 0;

        /// <summary>
        /// Похоже ли введённое на SHA-256: ровно 64 шестнадцатеричных знака после
        /// уборки пробелов. Пустая строка не подходит — это отдельный случай
        /// (<see cref="IsBlank"/>), поле необязательное.
        /// </summary>
        public static bool LooksValid(string? text)
        {
            string normalized = Normalize(text);
            if (normalized.Length != HexLength) return false;

            foreach (char c in normalized)
                if (!IsHexDigit(c)) return false;

            return true;
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
