using System.Collections;
using System.Collections.Generic;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>
    /// Список разделов, который наполняют детекторы разметок и зонды файловых систем.
    ///
    /// Отдельный тип, а не голый список, по двум причинам. Во-первых, у автоматических
    /// имён вида <c>part_07</c> и <c>gap_02</c> сквозная нумерация, и счётчики должны
    /// жить рядом с самим списком, иначе их приходится таскать параметрами через весь
    /// разбор — так было в оригинале, через глобальные переменные. Во-вторых, это
    /// единственное место, куда добавляются записи, и потому единственное место, где
    /// можно что-то про них утверждать.
    /// </summary>
    public sealed class PartitionTable : IEnumerable<PartitionEntry>
    {
        private readonly List<PartitionEntry> _items = new();

        public IReadOnlyList<PartitionEntry> Items => _items;

        public int Count => _items.Count;

        public PartitionEntry this[int index] => _items[index];

        /// <summary>Счётчик для автоматических имён <c>part_NN</c>.</summary>
        public int AutoPartCounter { get; set; }

        /// <summary>Счётчик для имён промежутков <c>gap_NN</c>.</summary>
        public int GapCounter { get; set; }

        /// <summary>Счётчик записей EBR — их имена тоже нумеруются сквозным образом.</summary>
        public int EbrCounter { get; set; }

        public PartitionEntry Add(
            string name,
            long offset,
            long length,
            FsType fsType = FsType.Unknown,
            string comment = "",
            byte mbrStatus = 0,
            byte mbrType = 0)
        {
            var entry = new PartitionEntry
            {
                Name = name,
                Offset = offset,
                Length = length,
                FsType = fsType,
                Comment = comment,
                MbrStatus = mbrStatus,
                MbrType = mbrType
            };

            _items.Add(entry);
            return entry;
        }

        public void Add(PartitionEntry entry) => _items.Add(entry);

        /// <summary>Добавляет раздел с именем <c>part_NN</c>, продолжая сквозную нумерацию.</summary>
        public PartitionEntry AddAutoNamed(long offset, long length, byte mbrStatus = 0, byte mbrType = 0)
        {
            AutoPartCounter++;
            return Add($"part_{AutoPartCounter:D2}", offset, length, FsType.Unknown, "", mbrStatus, mbrType);
        }

        public void RemoveAt(int index) => _items.RemoveAt(index);

        public void Clear()
        {
            _items.Clear();
            AutoPartCounter = 0;
            GapCounter = 0;
            EbrCounter = 0;
        }

        /// <summary>Заменяет содержимое списка — нужно шагам нормализации, которые его перестраивают.</summary>
        public void Replace(IEnumerable<PartitionEntry> entries)
        {
            _items.Clear();
            _items.AddRange(entries);
        }

        public IEnumerator<PartitionEntry> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
