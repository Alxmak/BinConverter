using System.Linq;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Проверка готовой таблицы разделов. Смысл проверки в том, что извлечение обрезает
    /// чтение по концу дампа и делает это молча: раздел с границей за концом файла
    /// извлекается в файл короче заявленного, и снаружи он выглядит целым. Валидатор
    /// ничего не чинит — он только называет то, что иначе осталось бы незамеченным.
    /// </summary>
    public class PartitionTableValidatorTests
    {
        private const long DumpSize = 0x10000;

        private static PartitionTable TableOf(params (string Name, long Offset, long Length)[] parts)
        {
            var table = new PartitionTable();
            foreach (var (name, offset, length) in parts) table.Add(name, offset, length);
            return table;
        }

        private static PartitionIssueKind[] KindsOf(PartitionTable table, long dumpSize = DumpSize) =>
            PartitionTableValidator.Validate(table, dumpSize).Select(i => i.Kind).ToArray();

        [Fact]
        public void CleanTable_HasNothingToReport()
        {
            var table = TableOf(("boot", 0, 0x1000), ("system", 0x1000, 0x2000), ("data", 0x3000, 0xD000));

            Assert.Empty(PartitionTableValidator.Validate(table, DumpSize));
        }

        [Fact]
        public void PartitionEndingPastTheDump_IsReportedWithTheMissingAmount()
        {
            var table = TableOf(("data", 0xF000, 0x2000));

            var issue = Assert.Single(PartitionTableValidator.Validate(table, DumpSize));

            Assert.Equal(PartitionIssueKind.EndsBeyondDump, issue.Kind);
            Assert.Equal("data", issue.Name);
            // 0xF000 + 0x2000 − 0x10000: столько байт не хватает до заявленного размера.
            Assert.Equal(0x1000, issue.Extra);
        }

        [Fact]
        public void PartitionStartingPastTheDump_IsADifferentCase()
        {
            // Такой раздел не просто обрежется — он не даст вообще ничего, и сказать
            // об этом надо иначе.
            var table = TableOf(("tail", 0x20000, 0x1000));

            var issue = Assert.Single(PartitionTableValidator.Validate(table, DumpSize));

            Assert.Equal(PartitionIssueKind.StartsBeyondDump, issue.Kind);
            Assert.Equal(DumpSize, issue.Extra);
        }

        [Fact]
        public void HugeLength_DoesNotOverflowIntoAPass()
        {
            // Обычное «смещение + длина» здесь даёт отрицательное число, то есть
            // проверка границы прошла бы успешно — как раз там, где не должна.
            var table = TableOf(("broken", 0x1000, long.MaxValue));

            Assert.Equal(new[] { PartitionIssueKind.EndsBeyondDump }, KindsOf(table));
        }

        [Fact]
        public void EndExactlyAtTheDumpSize_IsFine()
        {
            var table = TableOf(("all", 0, DumpSize));

            Assert.Empty(PartitionTableValidator.Validate(table, DumpSize));
        }

        [Fact]
        public void OverlappingPartitions_AreReportedOnce()
        {
            var table = TableOf(("boot", 0x1000, 0x2000), ("system", 0x2000, 0x1000));

            var issue = Assert.Single(PartitionTableValidator.Validate(table, DumpSize));

            Assert.Equal(PartitionIssueKind.Overlap, issue.Kind);
            Assert.Equal("boot", issue.Name);
            Assert.Equal("system", issue.OtherName);
        }

        [Fact]
        public void OverlapIsFoundEvenWhenTheTableIsNotSorted()
        {
            // Валидатор не имеет права полагаться на то, что до него звали нормализатор.
            var table = TableOf(("system", 0x2000, 0x1000), ("boot", 0x1000, 0x2000));

            Assert.Equal(new[] { PartitionIssueKind.Overlap }, KindsOf(table));
        }

        [Fact]
        public void TouchingPartitions_AreNotAnOverlap()
        {
            var table = TableOf(("boot", 0x1000, 0x1000), ("system", 0x2000, 0x1000));

            Assert.Empty(PartitionTableValidator.Validate(table, DumpSize));
        }

        [Fact]
        public void ExactRepeat_IsReported()
        {
            var table = TableOf(("boot", 0x1000, 0x1000), ("data", 0x2000, 0x1000), ("boot", 0x1000, 0x1000));

            // Повтор перекрывается сам с собой, поэтому замечаний два — но повтор среди них есть.
            Assert.Contains(PartitionIssueKind.Duplicate, KindsOf(table));
        }

        [Fact]
        public void NegativeValues_AreReportedAndDoNotBreakTheRest()
        {
            var table = TableOf(("bad-offset", -1, 0x1000), ("open-end", 0x1000, PartitionEntry.ExtendsToEnd));

            var kinds = KindsOf(table);

            Assert.Contains(PartitionIssueKind.NegativeOffset, kinds);
            Assert.Contains(PartitionIssueKind.NegativeLength, kinds);
        }

        [Fact]
        public void UnknownDumpSize_ChecksOnlyWhatDoesNotDependOnIt()
        {
            // Размер дампа неизвестен — про границы сказать нечего, а про перекрытие есть.
            var table = TableOf(("boot", 0x1000, 0x2000), ("system", 0x2000, 0x1000));

            Assert.Equal(new[] { PartitionIssueKind.Overlap }, KindsOf(table, dumpSize: 0));
        }

        [Fact]
        public void ValidateDoesNotTouchTheTable()
        {
            var table = TableOf(("system", 0x2000, 0x1000), ("boot", 0x1000, 0x2000));

            PartitionTableValidator.Validate(table, DumpSize);

            // Порядок и значения остались ровно теми, что были: чинить — не его дело.
            Assert.Equal(new[] { "system", "boot" }, table.Items.Select(p => p.Name).ToArray());
            Assert.Equal(0x2000, table[0].Offset);
        }

        [Fact]
        public void EveryKindOfIssue_HasItsOwnMessageNamingThePartition()
        {
            // У Describe есть ветка «всё остальное» — та, что описывает повтор записи.
            // Значит, новый вид замечания, добавленный в перечисление и забытый здесь,
            // молча начнёт читаться как «раздел встречается дважды»: замечание в журнале
            // будет, но не про то. Ни компилятор, ни остальные тесты этого не заметят.
            var messages = new List<string>();

            foreach (PartitionIssueKind kind in Enum.GetValues<PartitionIssueKind>())
            {
                var issue = new PartitionIssue(kind, "system", 0x1000, 0x2000, 0x300, "userdata");
                string text = issue.Describe();

                Assert.False(string.IsNullOrWhiteSpace(text), $"{kind}: пустое описание");
                Assert.Contains("system", text);
                messages.Add(text);
            }

            // Все описания разные — то есть каждый вид попал в свою ветку, а не в общую.
            Assert.Equal(messages.Count, messages.Distinct().Count());
        }
    }
}
