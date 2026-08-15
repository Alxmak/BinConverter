using System;

namespace TweakFirmware.Core.Localization
{
    /// <summary>Форма существительного при числе.</summary>
    public enum PluralForm
    {
        /// <summary>1 файл, 21 файл.</summary>
        One,

        /// <summary>2 файла, 23 файла.</summary>
        Few,

        /// <summary>5 файлов, 11 файлов.</summary>
        Many
    }

    /// <summary>
    /// Выбор формы существительного по числу.
    ///
    /// Подставлять одну форму на все случаи нельзя: в русском их три, и «Все 2 файлов
    /// различаются между собой» — первое, что бросается в глаза в готовом окне.
    /// Правило неочевидное (11–14 идут по «многим», хотя оканчиваются на 1, 2, 3, 4),
    /// поэтому оно живёт здесь и проверено тестами, а не разбросано по ViewModel.
    /// </summary>
    public static class PluralForms
    {
        /// <summary>
        /// Выбор формы — чистая функция от числа и языка, без обращения к словарю:
        /// так правило можно прогнать на любых числах, не трогая текущий язык программы.
        /// </summary>
        public static PluralForm Select(int count, string language)
        {
            count = Math.Abs(count);

            // В английском форм две: «file» только для единицы, всё остальное — «files».
            // Русское правило здесь не подходит: 21 file, а не 21 files.
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
                return count == 1 ? PluralForm.One : PluralForm.Many;

            // Одиннадцать–четырнадцать выпадают из общего правила: «11 файлов»,
            // хотя по последней цифре напрашивалось бы «11 файл».
            if (count % 100 is >= 11 and <= 14) return PluralForm.Many;

            return (count % 10) switch
            {
                1 => PluralForm.One,
                2 or 3 or 4 => PluralForm.Few,
                _ => PluralForm.Many
            };
        }

        /// <summary>«файл», «файла» или «файлов» на текущем языке программы.</summary>
        public static string Files(int count) => Strings.Get(Select(count, Strings.CurrentLanguage) switch
        {
            PluralForm.One => "Plural_FileOne",
            PluralForm.Few => "Plural_FileFew",
            _ => "Plural_FileMany"
        });
    }
}
