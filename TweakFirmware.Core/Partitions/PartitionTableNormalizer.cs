using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>
    /// Приведение списка разделов в пригодный вид после того, как детекторы в него
    /// накидали находок.
    ///
    /// Детекторы намеренно ничего не проверяют: каждый знает только свою таблицу и
    /// добавляет то, что в ней прочитал. Порядок, промежутки, дубликаты и мусор в именах —
    /// забота этого класса. Шаги вызываются в строго определённом порядке
    /// (<see cref="FixNames"/> → <see cref="SortByOffset"/> → <see cref="AddGaps"/> →
    /// <see cref="SortByOffset"/> → <see cref="RemoveDuplicates"/>), и вторая сортировка
    /// не лишняя: вставка промежутков дописывает записи в конец списка.
    /// </summary>
    public static class PartitionTableNormalizer
    {
        /// <summary>
        /// Обрезает имя на первом управляющем символе.
        ///
        /// Нужно потому, что имена читаются из бинарных таблиц фиксированной длины, и в
        /// повреждённом или просто нестандартном дампе туда попадает что угодно. Такое имя
        /// нельзя ни показать в таблице, ни подставить в имя файла.
        /// </summary>
        public static void FixNames(PartitionTable table)
        {
            foreach (var part in table.Items)
            {
                string name = part.Name;
                for (int i = 0; i < name.Length; i++)
                {
                    if (name[i] < 0x20)
                    {
                        part.Name = name[..i];
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Сортировка по возрастанию смещения.
        ///
        /// Сортировка устойчивая: разделы с одинаковым смещением сохраняют тот порядок,
        /// в котором их нашли. В оригинале на этом месте была выборка минимума с переносом
        /// в конец массива, которая такие записи переставляла местами — но не потому, что
        /// так задумано, а как побочный эффект алгоритма.
        /// </summary>
        public static void SortByOffset(PartitionTable table) =>
            table.Replace(table.Items.OrderBy(p => p.Offset).ToList());

        /// <summary>
        /// Разворачивает открытые концы и заполняет пустоты записями <c>gap_NN</c>.
        ///
        /// Возвращает размер дампа, из которого считались границы: он может увеличиться.
        /// Так бывает, когда строка mtdparts описывает микросхему большего объёма, чем
        /// имеющийся дамп, — тогда за размер принимается степень двойки, заведомо
        /// перекрывающая последнее смещение.
        /// </summary>
        public static long AddGaps(PartitionTable table, long logicalSize)
        {
            if (table.Count == 0) return logicalSize;

            var last = table[table.Count - 1];
            if (last.HasOpenEnd)
            {
                if (last.Offset > logicalSize)
                    logicalSize = NextPowerOfTwoAbove(last.Offset);

                last.Length = logicalSize - last.Offset;
            }

            // Число разделов запоминается заранее: дописанные промежутки сами
            // просматриваться не должны.
            int realCount = table.Count;
            long processed = 0;

            for (int i = 0; processed < logicalSize; i++)
            {
                if (i == realCount)
                {
                    AddGap(table, processed, logicalSize - processed);
                    break;
                }

                if (processed < table[i].Offset)
                    AddGap(table, processed, table[i].Offset - processed);

                processed = table[i].Offset + table[i].Length;
            }

            return logicalSize;
        }

        private static void AddGap(PartitionTable table, long offset, long length)
        {
            table.GapCounter++;
            table.Add($"gap_{table.GapCounter:D2}", offset, length);
        }

        /// <summary>
        /// Наименьшая степень двойки, заведомо превышающая значение. «Заведомо» здесь
        /// означает на разряд больше, чем нужно математически, — так считает оригинал,
        /// и на дампах, где эта ветка срабатывает, запас оказывается уместным.
        /// </summary>
        private static long NextPowerOfTwoAbove(long value)
        {
            int digits = 0;
            for (long v = value; v > 0; v >>= 1) digits++;
            return 1L << (digits + 1);
        }

        /// <summary>
        /// Убирает соседние записи, совпадающие смещением, длиной и именем.
        ///
        /// Такие пары появляются штатно: один и тот же раздел может попасть в список
        /// и из таблицы разметки, и из резервной копии этой таблицы.
        /// </summary>
        public static void RemoveDuplicates(PartitionTable table)
        {
            for (int i = table.Count - 1; i > 0; i--)
            {
                var a = table[i];
                var b = table[i - 1];
                if (a.Offset == b.Offset && a.Length == b.Length && a.Name == b.Name)
                    table.RemoveAt(i);
            }
        }

        /// <summary>
        /// Помечает разделы, заполненные целиком нулями или байтами 0xFF.
        ///
        /// Это подсказка мастеру: такой раздел в дампе пустой, восстанавливать из него
        /// нечего. Оригинал сравнивал прочитанное восьмибайтовое слово с 0xFF вместо
        /// 0xFFFFFFFFFFFFFFFF, из-за чего стёртые области NAND — а это как раз самый
        /// частый случай — пустыми не считались никогда. Здесь проверка сделана так, как
        /// описана в комментарии автора: «целиком забитые 0x00 или 0xFF».
        /// </summary>
        public static void MarkEmpty(
            PartitionTable table,
            IDumpReader dump,
            IProgress<AnalysisProgress>? progress = null,
            CancellationToken ct = default)
        {
            const int ChunkSize = 0x1000;
            var buffer = new byte[ChunkSize];

            for (int i = 0; i < table.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                if (IsEmpty(dump, table[i], buffer))
                {
                    var part = table[i];
                    part.Comment = string.IsNullOrEmpty(part.Comment)
                        ? Strings.Get("Extract_CommentEmpty")
                        : part.Comment + ", " + Strings.Get("Extract_CommentEmpty");
                }

                progress?.Report(new AnalysisProgress(Strings.Get("Extract_StageCheckEmpty"), i + 1, table.Count));
            }
        }

        private static bool IsEmpty(IDumpReader dump, PartitionEntry part, byte[] buffer)
        {
            long offset = part.Offset;
            long remaining = part.Length;
            if (remaining <= 0) return false;

            while (remaining > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, remaining);
                int read = dump.Read(offset, buffer.AsSpan(0, chunk));
                if (read <= 0) return false;

                for (int i = 0; i < read; i++)
                    if (buffer[i] != 0x00 && buffer[i] != 0xFF) return false;

                offset += chunk;
                remaining -= chunk;
            }

            return true;
        }

        /// <summary>
        /// Раскладывает плохие блоки микросхемы по разделам, в которые они попали.
        /// Блоки задаются номерами, поэтому нужен их размер — он определяется тем же
        /// детектором, который прочитал таблицу плохих блоков.
        /// </summary>
        public static void AttachBadBlocks(
            PartitionTable table,
            IReadOnlyList<long> badBlockNumbers,
            long blockSize)
        {
            if (blockSize <= 0) return;

            foreach (long block in badBlockNumbers)
            {
                long address = block * blockSize;
                foreach (var part in table.Items)
                {
                    if (address > part.Offset && address < part.Offset + part.Length)
                        part.BadBlocks++;
                }
            }
        }

        /// <summary>
        /// Полный проход в правильном порядке. Возвращает размер дампа, из которого
        /// считались границы, — см. <see cref="AddGaps"/>.
        /// </summary>
        public static long Normalize(PartitionTable table, long logicalSize)
        {
            FixNames(table);
            SortByOffset(table);
            long size = AddGaps(table, logicalSize);
            SortByOffset(table);
            RemoveDuplicates(table);
            return size;
        }
    }
}
