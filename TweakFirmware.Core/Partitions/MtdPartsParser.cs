using System;
using System.Collections.Generic;
using System.Globalization;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>Что получилось при разборе строки со списком разделов.</summary>
    public sealed class MtdPartsResult
    {
        /// <summary>Удалось ли извлечь хотя бы один раздел.</summary>
        public bool Success => Parts.Count > 0;

        public List<PartitionEntry> Parts { get; } = new();

        /// <summary>Записи, которые разобрать не вышло, — для журнала.</summary>
        public List<string> Rejected { get; } = new();

        /// <summary>Разбор оборвался посреди строки: дальше пошёл уже не список разделов.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Разбор строки параметров ядра Linux со списком разделов.
    ///
    /// Это самый переиспользуемый кусок всего разбора дампа: восемь разных разметок —
    /// от MTK и MSTAR до HiSilicon и Rockchip — устроены одинаково. Они лишь по-разному
    /// находят в дампе блок с текстом, а дальше отдают его сюда. Строка выглядит так:
    ///
    ///   mtdparts=mt53xx-emmc:2M(uboot),2M(uboot_env),256k(part_02),8M(perm),2048M(data)
    ///   2560k@0x380000(MBOOT),2560k(MBOOTBAK),4m(UBILD),40m(CONF),9m(tee),-(NA)
    ///   0x00002000@0x00002000(uboot),0x00006000@0x00006000(kernel),-@0x00204000(userdata)
    ///
    /// Размер записывается с множителем (k, M) или шестнадцатеричным числом; смещение,
    /// если оно есть, идёт после «@», а если его нет — раздел начинается там, где кончился
    /// предыдущий. Имя раздела в скобках, всё после закрывающей скобки — примечание: так
    /// в дампах встречается пометка шифрования, например <c>8M(perm)enc</c>.
    /// </summary>
    public static class MtdPartsParser
    {
        /// <summary>Значение длины, означающее «раздел до конца дампа».</summary>
        private const long OpenEnd = PartitionEntry.ExtendsToEnd;

        private enum RecordOutcome
        {
            Parsed,

            /// <summary>Запись испорчена, но список продолжается.</summary>
            Skip,

            /// <summary>Это уже не запись списка — дальше разбирать нечего.</summary>
            EndOfList
        }

        public static MtdPartsResult Parse(string table)
        {
            var result = new MtdPartsResult();
            if (string.IsNullOrEmpty(table)) return result;

            // Префикс до двоеточия — имя устройства (mtdparts=mt53xx-nand), а не данные.
            int colon = table.IndexOf(':');
            if (colon > 0) table = table[(colon + 1)..];

            long runningOffset = 0;

            foreach (string record in SplitRecords(table, result))
            {
                var outcome = TryParseRecord(record, ref runningOffset, out var entry);

                if (outcome == RecordOutcome.EndOfList)
                {
                    result.Truncated = true;
                    break;
                }

                if (outcome == RecordOutcome.Skip)
                {
                    result.Rejected.Add(record);
                    continue;
                }

                result.Parts.Add(entry!);
            }

            return result;
        }

        /// <summary>
        /// Режет строку по запятым и останавливается там, где список разделов кончился.
        ///
        /// Признак конца — пробел или знак процента в оставшемся тексте. Условие выглядит
        /// странно (складываются два индекса, каждый равен -1 при отсутствии), но
        /// перенесено из оригинала дословно: строка вырезана из дампа, вокруг неё
        /// произвольные данные, и любое уточнение этого условия меняет, где именно разбор
        /// решит остановиться на реальной прошивке.
        /// </summary>
        private static List<string> SplitRecords(string table, MtdPartsResult result)
        {
            var records = new List<string>();

            while (true)
            {
                int comma = table.IndexOf(',');
                if (comma > 0)
                {
                    int whitespace = table.IndexOf(' ');
                    int percent = table.IndexOf('%');
                    if (whitespace + percent > 0)
                    {
                        result.Truncated = true;
                        break;
                    }

                    records.Add(table[..comma]);
                    table = table[(comma + 1)..];
                }
                else
                {
                    // Последняя запись идёт без запятой. Одиночная запись без единой запятой
                    // списком не считается — слишком похоже на случайное совпадение в данных.
                    if (records.Count > 0) records.Add(table);
                    break;
                }
            }

            return records;
        }

        private static RecordOutcome TryParseRecord(string record, ref long runningOffset, out PartitionEntry? entry)
        {
            entry = null;

            int open = record.IndexOf('(');
            int close = record.IndexOf(')');

            // Скобок нет или они перепутаны — значит, это уже не запись списка разделов.
            if (open < 0 || close < 0 || open > close) return RecordOutcome.EndOfList;

            string name = record[(open + 1)..close];
            string comment = record[(close + 1)..];

            // До открывающей скобки — числа, после неё — имя и примечание.
            string numbers = record[..open];

            // «@» учитываем только до скобок: внутри имени это просто символ.
            int at = numbers.IndexOf('@');

            long offset = runningOffset;
            if (at > 0)
            {
                long? explicitOffset = ParseSize(numbers[(at + 1)..]);
                if (explicitOffset is null || explicitOffset == OpenEnd) return RecordOutcome.Skip;

                offset = explicitOffset.Value;
                numbers = numbers[..at];
            }

            long? length = ParseSize(numbers);
            if (length is null) return RecordOutcome.Skip;

            entry = new PartitionEntry
            {
                Name = name,
                Offset = offset,
                Length = length.Value,
                Comment = comment
            };

            // Следующий раздел без «@» начнётся сразу за этим. У раздела с открытым концом
            // длины ещё нет, но он в списке всегда последний.
            runningOffset = length.Value > 0 ? offset + length.Value : offset;

            return RecordOutcome.Parsed;
        }

        /// <summary>
        /// Число с множителем из строки параметров ядра.
        ///
        /// Понимает <c>-</c> (до конца дампа), шестнадцатеричное <c>0x…</c>, десятичное
        /// с множителем <c>k</c>/<c>K</c>, <c>m</c>/<c>M</c>, <c>g</c>/<c>G</c> и просто
        /// десятичное. Последние два случая оригинал не понимал: он печатал ошибку и
        /// возвращал -1, из-за чего такая запись превращалась в раздел до конца дампа.
        /// Их поддержка ничего сломать не может — только разбирает то, на чём раньше
        /// список обрывался.
        ///
        /// Возвращает <c>null</c>, если это вообще не число.
        /// </summary>
        public static long? ParseSize(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            text = text.Trim();
            if (text == "-") return OpenEnd;

            if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
            {
                return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)
                    ? hex
                    : null;
            }

            long multiplier = text[^1] switch
            {
                'k' or 'K' => 1024L,
                'm' or 'M' => 1024L * 1024,
                'g' or 'G' => 1024L * 1024 * 1024,
                _ => 1L
            };

            if (multiplier != 1) text = text[..^1];
            if (text.Length == 0) return null;

            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                ? multiplier * value
                : null;
        }

        /// <summary>Разобрать строку прямо в таблицу разделов; возвращает число добавленных записей.</summary>
        public static int ParseInto(string table, PartitionTable target)
        {
            var result = Parse(table);
            foreach (var part in result.Parts) target.Add(part);
            return result.Parts.Count;
        }
    }
}
