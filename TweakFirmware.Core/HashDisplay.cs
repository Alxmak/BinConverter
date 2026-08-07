using System;
using System.Text;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Разбивка хэша на строки для показа в диалогах.
    ///
    /// SHA-256 — это 64 знака без единого пробела. Окно сообщения переносит текст только
    /// по пробелам, поэтому такую «строку-слово» оно не переносит вовсе, а всё, что не
    /// поместилось по ширине, просто обрезает — и пользователь видит хвост многоточием
    /// вместо контрольной суммы. Перенос приходится расставлять самим.
    ///
    /// Только для диалогов: в журнал и в поля ввода хэш должен попадать одной строкой,
    /// иначе его нельзя будет скопировать и сравнить.
    /// </summary>
    public static class HashDisplay
    {
        /// <summary>
        /// Сколько знаков в строке. 32 — половина SHA-256, то есть хэш всегда ложится
        /// ровно в две строки, и обе с большим запасом влезают в ширину окна.
        /// </summary>
        public const int LineLength = 32;

        /// <summary>
        /// Разбивает хэш на строки по <paramref name="lineLength"/> знаков.
        /// Ничего не добавляет и не теряет: если убрать переносы, получится исходная строка.
        /// </summary>
        public static string Wrap(string? hash, int lineLength = LineLength)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            if (lineLength <= 0 || hash.Length <= lineLength) return hash;

            var sb = new StringBuilder(hash.Length + hash.Length / lineLength + 1);
            for (int offset = 0; offset < hash.Length; offset += lineLength)
            {
                if (offset > 0) sb.Append('\n');
                sb.Append(hash, offset, Math.Min(lineLength, hash.Length - offset));
            }
            return sb.ToString();
        }
    }
}
