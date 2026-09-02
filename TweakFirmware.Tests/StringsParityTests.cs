using System.Reflection;
using System.Text.RegularExpressions;
using TweakFirmware.Core.Localization;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Сверка русского и английского словарей между собой.
    ///
    /// Само по себе расхождение молчаливое: <see cref="Strings.Get"/> на недостающем
    /// ключе откатывается к русскому тексту, так что английский интерфейс просто
    /// заговорит по-русски — и заметит это только тот, кто переключил язык. А вот
    /// разошедшийся набор подстановок молчать не станет: <see cref="Strings.Format"/>
    /// уронит окно на FormatException, если в одном языке аргументов больше.
    ///
    /// Порядок ключей держится тем же тестом не из аккуратности: словари правят руками
    /// и обычно копированием соседней строки, а найти пропавший ключ глазами в списке
    /// из четырёхсот с лишним можно, только если оба списка идут одинаково.
    /// </summary>
    public class StringsParityTests
    {
        // Числа со специальным форматом («{1:N0}») считаем вместе с форматом: если байты
        // выводятся с разделителями разрядов только в одном языке, это тоже расхождение.
        private static readonly Regex Placeholder = new(@"\{(\d+[^{}]*)\}", RegexOptions.Compiled);

        /// <summary>
        /// Словари приватные, и открывать их наружу незачем — тесту они нужны целиком,
        /// а через Strings.Get расхождение как раз и не видно: он подставляет русский текст
        /// вместо недостающего английского.
        ///
        /// Порядок перечисления Dictionary совпадает с порядком добавления, пока из него
        /// ничего не удаляли, — а здесь он заполняется инициализатором и не меняется.
        /// </summary>
        private static List<KeyValuePair<string, string>> Table(string name)
        {
            var field = typeof(Strings).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"В Strings нет словаря {name} — если его переименовали, поправьте и этот тест.");

            var table = field.GetValue(null) as Dictionary<string, string>
                ?? throw new InvalidOperationException($"Словарь {name} оказался не Dictionary<string, string>.");

            return table.ToList();
        }

        private static List<KeyValuePair<string, string>> Ru() => Table("Ru");

        private static List<KeyValuePair<string, string>> En() => Table("En");

        [Fact]
        public void BothDictionaries_HaveTheSameKeys()
        {
            var ru = Ru().Select(pair => pair.Key).ToList();
            var en = En().Select(pair => pair.Key).ToList();

            var missingInEn = ru.Except(en).ToList();
            var missingInRu = en.Except(ru).ToList();

            Assert.True(missingInEn.Count == 0, "Нет в английском словаре: " + string.Join(", ", missingInEn));
            Assert.True(missingInRu.Count == 0, "Нет в русском словаре: " + string.Join(", ", missingInRu));
        }

        [Fact]
        public void BothDictionaries_KeepKeysInTheSameOrder()
        {
            var ru = Ru().Select(pair => pair.Key).ToList();
            var en = En().Select(pair => pair.Key).ToList();

            for (int i = 0; i < Math.Min(ru.Count, en.Count); i++)
                Assert.True(ru[i] == en[i],
                    $"Порядок разошёлся на позиции {i}: в русском «{ru[i]}», в английском «{en[i]}».");

            Assert.Equal(ru.Count, en.Count);
        }

        [Fact]
        public void Placeholders_AreTheSameInBothLanguages()
        {
            var en = En().ToDictionary(pair => pair.Key, pair => pair.Value);

            foreach (var (key, russian) in Ru())
            {
                if (!en.TryGetValue(key, out var english)) continue;   // это ловит соседний тест

                var inRu = Placeholder.Matches(russian).Select(m => m.Groups[1].Value).OrderBy(s => s, StringComparer.Ordinal);
                var inEn = Placeholder.Matches(english).Select(m => m.Groups[1].Value).OrderBy(s => s, StringComparer.Ordinal);

                Assert.True(inRu.SequenceEqual(inEn),
                    $"Ключ «{key}»: подстановки разошлись — в русском {{{string.Join(", ", inRu)}}}, " +
                    $"в английском {{{string.Join(", ", inEn)}}}.");
            }
        }

        [Fact]
        public void LineBreaks_AreTheSameInBothLanguages()
        {
            // Переводы строки в этих текстах несут разметку: ими отделяется заголовок
            // сообщения от подробностей. Потерявшийся перенос слепляет окно в одну простыню.
            var en = En().ToDictionary(pair => pair.Key, pair => pair.Value);

            foreach (var (key, russian) in Ru())
            {
                if (!en.TryGetValue(key, out var english)) continue;

                Assert.True(russian.Count(c => c == '\n') == english.Count(c => c == '\n'),
                    $"Ключ «{key}»: переводов строки в русском {russian.Count(c => c == '\n')}, " +
                    $"в английском {english.Count(c => c == '\n')}.");
            }
        }

        [Fact]
        public void NoValue_IsEmpty()
        {
            foreach (var (name, table) in new[] { ("Ru", Ru()), ("En", En()) })
                foreach (var (key, value) in table)
                    Assert.True(!string.IsNullOrWhiteSpace(value), $"Словарь {name}: у ключа «{key}» пустое значение.");
        }
    }
}
