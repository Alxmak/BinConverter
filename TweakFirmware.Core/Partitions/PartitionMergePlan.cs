using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>Один кусок будущего образа: файл, который ляжет по своему адресу.</summary>
    public sealed record MergePiece(string Path, string Name, long Offset, long Length);

    /// <summary>Место, которое ни один файл не покрывает.</summary>
    public readonly record struct MergeGap(long Offset, long Length);

    public enum MergeIssueKind
    {
        /// <summary>Файл на диске не того размера, который записан в его имени.</summary>
        SizeMismatch,

        /// <summary>Два куска претендуют на одни и те же байты образа.</summary>
        Overlap
    }

    /// <summary>Почему собрать нельзя: что не так и с каким файлом.</summary>
    public readonly record struct MergeIssue(
        MergeIssueKind Kind,
        string Name,
        string OtherName,
        long Offset,
        long Expected,
        long Actual)
    {
        /// <summary>
        /// Строка для журнала и для сообщения об отказе — так же, как у замечаний
        /// к таблице разделов (<see cref="PartitionIssue.Describe"/>): числа
        /// шестнадцатеричные, без единиц измерения.
        /// </summary>
        public string Describe() => Kind switch
        {
            MergeIssueKind.SizeMismatch =>
                Strings.Format("PartitionMerge_IssueSizeMismatch", Name, Actual, Expected),
            _ =>
                Strings.Format("Common_IssueOverlap", Name, OtherName, Offset)
        };
    }

    /// <summary>
    /// Что получится, если сложить извлечённые разделы обратно в один файл.
    ///
    /// Всё, что нужно для обратной сборки, лежит в именах самих файлов: извлечение
    /// называет их <c>USER_0x{адрес}_0x{размер}_{имя}.bin</c>, и по такому имени видно,
    /// куда кусок ложится и какой он длины. Поэтому папка с разделами самодостаточна —
    /// ни таблицы, ни .udev рядом держать не нужно.
    ///
    /// План строится до записи и отвечает на три вопроса: что войдёт в образ, какого он
    /// получится размера и не мешают ли куски друг другу. Последнее важнее всего: файл
    /// не того размера или два куска на одних байтах — это молча испорченная прошивка,
    /// и узнать об этом надо до того, как начнётся запись, а не по несовпавшему хэшу.
    /// </summary>
    public sealed class PartitionMergePlan
    {
        /// <summary>Куски по возрастанию адреса.</summary>
        public IReadOnlyList<MergePiece> Pieces { get; init; } = Array.Empty<MergePiece>();

        /// <summary>Промежутки между кусками — их заполняет сама сборка.</summary>
        public IReadOnlyList<MergeGap> Gaps { get; init; } = Array.Empty<MergeGap>();

        /// <summary>Имена файлов, которые на извлечённый раздел не похожи.</summary>
        public IReadOnlyList<string> SkippedFiles { get; init; } = Array.Empty<string>();

        /// <summary>Что мешает сборке. Пустой список — можно собирать.</summary>
        public IReadOnlyList<MergeIssue> Issues { get; init; } = Array.Empty<MergeIssue>();

        /// <summary>
        /// Размер будущего файла — конец последнего куска. Хвост за ним не дописывается:
        /// откуда программе знать, сколько его было, если этих байтов никто не извлекал.
        /// Если полный размер важен (а для записи в микросхему он важен), извлекать надо
        /// всю таблицу целиком, вместе с промежутками, — тогда последний кусок и кончается
        /// концом дампа.
        /// </summary>
        public long TotalSize { get; init; }

        /// <summary>Сколько байт придёт из файлов.</summary>
        public long DataSize { get; init; }

        /// <summary>Сколько байт придётся заполнить.</summary>
        public long GapSize => TotalSize - DataSize;

        public bool CanMerge => Pieces.Count > 0 && Issues.Count == 0;

        /// <summary>
        /// Осматривает папку. На диск ходит только здесь: размер каждого файла нужно
        /// сверить с тем, что записано в его имени.
        /// </summary>
        public static PartitionMergePlan Build(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new PartitionMergePlan();

            var pieces = new List<MergePiece>();
            var skipped = new List<string>();
            var issues = new List<MergeIssue>();

            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(path);

                if (!PartitionFileNaming.TryParse(name, out var info))
                {
                    skipped.Add(name);
                    continue;
                }

                long actual = new FileInfo(path).Length;
                if (actual != info.Length)
                {
                    // Не отбрасываем: человек должен увидеть, какой именно файл не сходится.
                    // Обрезать его до размера в имени нельзя — это молча испортит образ,
                    // а дописать недостающее нечем.
                    issues.Add(new MergeIssue(
                        MergeIssueKind.SizeMismatch, info.Name, "", info.Offset, info.Length, actual));
                    continue;
                }

                pieces.Add(new MergePiece(path, info.Name, info.Offset, info.Length));
            }

            // Сортировка по адресу, при равных адресах — по имени: порядок перечисления
            // файлов в папке зависит от файловой системы, а план должен получаться
            // одинаковым при каждом осмотре.
            pieces.Sort((a, b) => a.Offset != b.Offset
                ? a.Offset.CompareTo(b.Offset)
                : string.CompareOrdinal(a.Path, b.Path));

            var gaps = new List<MergeGap>();
            long cursor = 0;

            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];

                if (piece.Offset > cursor) gaps.Add(new MergeGap(cursor, piece.Offset - cursor));

                // Начаться раньше конца предыдущего кусок может только если предыдущий
                // был, — до первого куска курсор стоит на нуле, а адреса неотрицательны.
                if (piece.Offset < cursor)
                {
                    issues.Add(new MergeIssue(
                        MergeIssueKind.Overlap, pieces[i - 1].Name, piece.Name, piece.Offset, 0, cursor - piece.Offset));
                }

                cursor = Math.Max(cursor, piece.Offset + piece.Length);
            }

            return new PartitionMergePlan
            {
                Pieces = pieces,
                Gaps = gaps,
                SkippedFiles = skipped,
                Issues = issues,
                TotalSize = cursor,
                DataSize = pieces.Sum(p => p.Length)
            };
        }
    }
}
