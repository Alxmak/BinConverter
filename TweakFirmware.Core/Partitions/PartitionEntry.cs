using System;

namespace TweakFirmware.Core.Partitions
{
    /// <summary>
    /// Файловая система, опознанная внутри раздела. Набор в точности тот, что различает
    /// разбор дампа: добавлять сюда что-то без зонда, умеющего это находить, бессмысленно.
    /// </summary>
    public enum FsType
    {
        Unknown,
        Ext4,
        SquashFs,
        Fat16,
        Fat32,
        RomFs,
        CramFs,
        Vdfs,
        Tar
    }

    /// <summary>
    /// Один раздел дампа: где начинается, сколько занимает и что внутри.
    ///
    /// Смещение и длина всегда логические — то есть без служебных областей spare. Перевод
    /// в физические адреса делается только на выходе: при показе таблицы, при записи
    /// .udev со spare и при извлечении файлов.
    /// </summary>
    public sealed class PartitionEntry
    {
        public string Name { get; set; } = "";

        public long Offset { get; set; }

        /// <summary>
        /// Длина раздела. Знаковая намеренно: значение <see cref="ExtendsToEnd"/> приходит
        /// из записей вида <c>-(userdata)</c> в строке mtdparts и означает «до конца дампа».
        /// Настоящий размер подставляется позже, на шаге вставки промежутков, — раньше он
        /// просто неизвестен, потому что зависит от размера дампа и наличия spare.
        /// </summary>
        public long Length { get; set; }

        /// <summary>Значение длины, означающее «раздел тянется до конца дампа».</summary>
        public const long ExtendsToEnd = -1;

        public bool HasOpenEnd => Length < 0;

        public FsType FsType { get; set; } = FsType.Unknown;

        /// <summary>Примечание к разделу: комментарий из таблицы, метка «empty» и подобное.</summary>
        public string Comment { get; set; } = "";

        /// <summary>Тип раздела из записи MBR (0x83 Linux, 0xEE GPT и так далее).</summary>
        public byte MbrType { get; set; }

        /// <summary>Байт статуса из записи MBR: 0x80 — загрузочный, 0x00 — обычный.</summary>
        public byte MbrStatus { get; set; }

        /// <summary>Сколько плохих блоков микросхемы попало в этот раздел.</summary>
        public int BadBlocks { get; set; }

        /// <summary>Отмечен ли раздел для извлечения. В интерфейсе это галочка в таблице.</summary>
        public bool Selected { get; set; } = true;

        /// <summary>Конец раздела; для раздела с открытым концом смысла не имеет.</summary>
        public long End => Offset + Length;

        public PartitionEntry Clone() => (PartitionEntry)MemberwiseClone();

        /// <summary>Тот же раздел, но в физических адресах дампа NAND.</summary>
        public PartitionEntry ToPhysical(Dump.NandGeometry geometry)
        {
            if (geometry is null) throw new ArgumentNullException(nameof(geometry));

            var copy = Clone();
            copy.Offset = geometry.AddSpare(Offset);

            // Открытый конец пересчитывать нечего: длины ещё нет.
            if (Length > 0) copy.Length = geometry.AddSpare(Length);
            return copy;
        }

        public override string ToString() =>
            $"0x{Offset:X} 0x{Length:X} {Name}" + (FsType == FsType.Unknown ? "" : $" [{FsType}]");
    }
}
