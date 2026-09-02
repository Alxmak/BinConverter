using System;
using System.Collections.Generic;
using System.Linq;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>Чем именно запись таблицы вызывает вопросы.</summary>
    public enum PartitionIssueKind
    {
        /// <summary>Смещение меньше нуля — такого в дампе быть не может.</summary>
        NegativeOffset,

        /// <summary>Длина осталась отрицательной, то есть «до конца» так и не развернулось.</summary>
        NegativeLength,

        /// <summary>Раздел начинается за концом дампа — извлекать нечего.</summary>
        StartsBeyondDump,

        /// <summary>Раздел начинается внутри дампа, но кончается за ним — файл выйдет короче.</summary>
        EndsBeyondDump,

        /// <summary>Раздел заходит на соседний.</summary>
        Overlap,

        /// <summary>Та же запись встречается в таблице ещё раз.</summary>
        Duplicate
    }

    /// <summary>Одно замечание к таблице: что не так и с какой записью.</summary>
    public readonly record struct PartitionIssue(
        PartitionIssueKind Kind,
        string Name,
        long Offset,
        long Length,
        long Extra = 0,
        string OtherName = "")
    {
        /// <summary>
        /// Строка для журнала. Числа в шестнадцатеричном виде и без единиц измерения:
        /// так они сходятся с остальными строками разбора и не требуют склонения.
        /// </summary>
        public string Describe() => Kind switch
        {
            PartitionIssueKind.NegativeOffset =>
                Strings.Format("Extract_IssueNegativeOffset", Name, Offset),
            PartitionIssueKind.NegativeLength =>
                Strings.Format("Extract_IssueNegativeLength", Name, Length),
            PartitionIssueKind.StartsBeyondDump =>
                Strings.Format("Extract_IssueStartsBeyondDump", Name, Offset, Extra),
            PartitionIssueKind.EndsBeyondDump =>
                Strings.Format("Extract_IssueEndsBeyondDump", Name, Offset, Length, Extra),
            // Для перекрытия в Extra лежит начало соседнего раздела — то есть адрес,
            // с которого они наезжают друг на друга.
            PartitionIssueKind.Overlap =>
                Strings.Format("Common_IssueOverlap", Name, OtherName, Extra),
            _ =>
                Strings.Format("Extract_IssueDuplicate", Name, Offset, Length)
        };
    }

    /// <summary>
    /// Проверка готовой таблицы разделов перед тем, как ею начнут пользоваться.
    ///
    /// Ничего не исправляет — только называет. Исправление это дело
    /// <see cref="PartitionTableNormalizer"/>, и смешивать их нельзя: разбор чужого
    /// дампа должен оставаться разбором, а не тихим приведением к ожидаемому виду.
    /// Молчаливая правка тут особенно вредна — именно она и скрывала бы то, ради чего
    /// дамп открывают.
    ///
    /// Зачем это нужно на практике: извлечение читает файл и обрезает чтение по его
    /// концу (<c>Math.Min(length, размер - смещение)</c>). Раздел, у которого граница
    /// уходит за конец дампа, извлекается молча и получается короче заявленного —
    /// снаружи это выглядит как исправный файл. Здесь про это говорится вслух.
    /// </summary>
    public static class PartitionTableValidator
    {
        /// <summary>
        /// Смотрит таблицу и возвращает список замечаний. Пустой список — значит,
        /// придраться не к чему.
        /// </summary>
        /// <param name="table">Таблица после нормализации.</param>
        /// <param name="dumpSize">
        /// Логический размер дампа, то есть без служебных областей. Именно по нему
        /// обрезает чтение извлечение, поэтому и границы проверяются по нему, а не по
        /// размеру, который вывел нормализатор: у строки mtdparts он может описывать
        /// микросхему большего объёма, чем имеющийся файл. Ноль или меньше —
        /// размер неизвестен, границы не проверяются.
        /// </param>
        public static IReadOnlyList<PartitionIssue> Validate(PartitionTable table, long dumpSize)
        {
            var issues = new List<PartitionIssue>();

            foreach (var part in table.Items)
            {
                if (part.Offset < 0)
                    issues.Add(new PartitionIssue(PartitionIssueKind.NegativeOffset, part.Name, part.Offset, part.Length));

                if (part.Length < 0)
                    issues.Add(new PartitionIssue(PartitionIssueKind.NegativeLength, part.Name, part.Offset, part.Length));

                if (dumpSize <= 0 || part.Offset < 0 || part.Length < 0) continue;

                if (part.Offset >= dumpSize)
                {
                    issues.Add(new PartitionIssue(
                        PartitionIssueKind.StartsBeyondDump, part.Name, part.Offset, part.Length, dumpSize));
                    continue;
                }

                // Сложение проверяется на переполнение до того, как оно случится:
                // в испорченной таблице длина бывает близка к пределу типа, и обычное
                // «смещение + длина» дало бы отрицательное число, то есть проверка
                // границы прошла бы успешно там, где как раз и не должна.
                bool overflows = long.MaxValue - part.Offset < part.Length;
                if (overflows || part.Offset + part.Length > dumpSize)
                {
                    long missing = overflows ? long.MaxValue : part.Offset + part.Length - dumpSize;
                    issues.Add(new PartitionIssue(
                        PartitionIssueKind.EndsBeyondDump, part.Name, part.Offset, part.Length, missing));
                }
            }

            AddOverlaps(table, issues);
            AddDuplicates(table, issues);

            return issues;
        }

        /// <summary>
        /// Перекрытия ищутся по копии, отсортированной по смещению: сама таблица после
        /// нормализации и так отсортирована, но проверка не имеет права зависеть от
        /// того, звали нормализатор или нет, и тем более что-то в таблице двигать.
        /// </summary>
        private static void AddOverlaps(PartitionTable table, List<PartitionIssue> issues)
        {
            var sorted = table.Items
                .Where(p => p.Offset >= 0 && p.Length > 0)
                .OrderBy(p => p.Offset)
                .ThenBy(p => p.Length)
                .ToList();

            for (int i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var cur = sorted[i];

                if (long.MaxValue - prev.Offset < prev.Length) continue;   // уже названо как выход за конец

                if (prev.Offset + prev.Length > cur.Offset)
                    issues.Add(new PartitionIssue(
                        PartitionIssueKind.Overlap, prev.Name, prev.Offset, prev.Length, cur.Offset, cur.Name));
            }
        }

        /// <summary>
        /// Полные повторы — одно и то же имя на том же месте и той же длины. Соседние
        /// такие пары убирает нормализатор (одна и та же таблица, прочитанная и из
        /// основной копии, и из резервной), поэтому оставшийся повтор означает, что
        /// записи разъехались по списку, а это уже странно.
        /// </summary>
        private static void AddDuplicates(PartitionTable table, List<PartitionIssue> issues)
        {
            var seen = new HashSet<(string, long, long)>();

            foreach (var part in table.Items)
            {
                if (!seen.Add((part.Name, part.Offset, part.Length)))
                    issues.Add(new PartitionIssue(
                        PartitionIssueKind.Duplicate, part.Name, part.Offset, part.Length));
            }
        }
    }
}
