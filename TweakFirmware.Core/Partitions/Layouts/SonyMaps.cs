namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Точные карты разделов для дампов Sony на MediaTek.
    ///
    /// Это данные, а не код: у моделей, где разметка опознана до последнего раздела,
    /// границы просто выписаны. Новая модель добавляется строчками в таблицу, поэтому
    /// карты вынесены отдельно от детектора.
    ///
    /// Имена сохранены дословно, включая номера в part_NN: по ним называются извлечённые
    /// файлы, и мастера ищут их в готовых наборах разделов.
    /// </summary>
    public static class SonyMaps
    {
        public readonly record struct Entry(string Name, long Offset, long Length, FsType FsType = FsType.Unknown);

        /// <summary>Шасси GN5UR на процессоре MT5835.</summary>
        public static readonly Entry[] Gn5Ur =
        {
            new("uboot",      0x00000000, 0x00200000),
            new("uboot_02",   0x00400000, 0x00200000),
            new("part_02",    0x00C00000, 0x00040000),
            new("part_03",    0x00C60000, 0x00030000),
            new("part_04",    0x00CA0000, 0x00030000),
            new("perm",       0x00D00000, 0x00800000, FsType.Ext4),
            new("part_06",    0x01500000, 0x00010000),
            new("part_07",    0x01700000, 0x00100000),
            new("persist",    0x01800000, 0x06000000, FsType.Ext4),
            new("fat16_09",   0x07800000, 0x00400000, FsType.Fat16),
            new("part_10",    0x08000000, 0x00210000),
            new("part_11",    0x08900000, 0x00010000),
            new("part_12",    0x08A40000, 0x00010000),
            new("part_13",    0x08A60000, 0x00020000),
            new("part_14",    0x08E00000, 0x00010000),
            new("part_15",    0x08F40000, 0x00010000),
            new("part_16",    0x08F60000, 0x00020000),
            new("part_17",    0x09300000, 0x00010000),
            new("part_18",    0x09400000, 0x00010000),
            new("part_19",    0x09500000, 0x00010000),
            new("part_20",    0x09600000, 0x00010000),
            new("part_21",    0x09700000, 0x00060000),
            new("part_22",    0x09800000, 0x000A0000),
            new("part_23",    0x098E0000, 0x00020000),
            new("avb_24",     0x0C900000, 0x00010000),
            new("avb_25",     0x0CA00000, 0x00010000),
            new("part_26",    0x0CB00000, 0x003E0000),
            new("part_27",    0x0D0F0000, 0x003F0000),
            new("part_28",    0x0D6F0000, 0x00010000),
            new("fat16_29",   0x0DB00000, 0x10000000, FsType.Fat16),
            new("fat16_30",   0x1DB00000, 0x10000000, FsType.Fat16),
            new("part_31",    0x2DB00000, 0x08010000),
            new("part_32",    0x362F0000, 0x00020000),
            new("part_33",    0x36AF0000, 0x10010000),
            new("3rd_rw",     0x46B00000, 0x10000000, FsType.Ext4),
            new("part_35",    0x56B00000, 0x23C400000),
            new("fat16_36",   0x292F00000, 0x02000000, FsType.Fat16),
            new("part_37",    0x294F00000, 0x003F0000),
            new("part_38",    0x295420000, 0x00030000),
            new("part_39",    0x296FE0000, 0x00120000),
            new("data",       0x297100000, 0x10DD00000, FsType.Ext4)
        };

        /// <summary>Карта, снятая с дампа, у которого хвост разложен иначе.</summary>
        public static readonly Entry[] SkanDump =
        {
            new("uboot",      0x00000000, 0x00200000),
            new("uboot_02",   0x00400000, 0x00200000),
            new("part_02",    0x00C00000, 0x00030000),
            new("part_03",    0x00C60000, 0x00030000),
            new("part_04",    0x00CA0000, 0x00030000),
            new("perm",       0x00D00000, 0x00800000, FsType.Ext4),
            new("part_06",    0x01500000, 0x00010000),
            new("part_07",    0x01700000, 0x00100000),
            new("persist",    0x01800000, 0x06000000, FsType.Ext4),
            new("fat16_09",   0x07800000, 0x00400000, FsType.Fat16),
            new("part_10",    0x08000000, 0x00210000),
            new("part_11",    0x08900000, 0x00010000),
            new("part_12",    0x08A40000, 0x00010000),
            new("part_13",    0x08A60000, 0x00020000),
            new("part_14",    0x08E00000, 0x00010000),
            new("part_15",    0x08F40000, 0x00010000),
            new("part_16",    0x08F60000, 0x00020000),
            new("part_17",    0x09300000, 0x00010000),
            new("part_18",    0x09400000, 0x00010000),
            new("part_19",    0x09500000, 0x00010000),
            new("part_20",    0x09600000, 0x00010000),
            new("part_21",    0x09700000, 0x00050000),
            new("part_22",    0x09800000, 0x00100000),
            new("avb_23",     0x0C900000, 0x00010000),
            new("avb_24",     0x0CA00000, 0x00010000),
            new("part_25",    0x0CB00000, 0x003D0000),
            new("part_26",    0x0D0F0000, 0x003F0000),
            new("part_27",    0x0D6F0000, 0x00010000),
            new("fat16_28",   0x0DB00000, 0x10000000, FsType.Fat16),
            new("fat16_29",   0x1DB00000, 0x10000000, FsType.Fat16),
            new("part_30",    0x2DB00000, 0x08010000),
            new("part_31",    0x362F0000, 0x00020000),
            new("part_32",    0x36AF0000, 0x10010000),
            new("3rd_rw",     0x46B00000, 0x10000000, FsType.Ext4),
            new("part_34",    0x56B00000, 0x23C400000),
            new("fat16_35",   0x292F00000, 0x02000000, FsType.Fat16),
            new("data",       0x294F00000, 0x10DD00000, FsType.Ext4)
        };
    }
}
