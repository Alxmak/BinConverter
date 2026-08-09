using System;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Телевизоры Sony на MediaTek с памятью eMMC.
    ///
    /// Таблицы разделов в таком дампе нет вообще — ни MBR, ни GPT, ни строки параметров
    /// ядра. Разметка восстанавливается по опорным точкам: местам, где заведомо должны
    /// лежать суперблок ext4, загрузочный сектор FAT16 или заголовок AVB. Три способа,
    /// по убыванию строгости: две точные карты известных моделей и плавающий поиск,
    /// который те же опорные точки ищет в окрестности и проверяет, что они идут по порядку.
    /// </summary>
    public sealed class SonyMtkEmmcDetector : ILayoutDetector
    {
        /// <summary>Шаг и выравнивание поиска опорных точек — один мегабайт.</summary>
        private const long AnchorStep = 0x100000;

        /// <summary>Какая из трёх карт подошла. Заполняется при опознании и нужна при чтении.</summary>
        private enum MapKind { None, Gn5Ur, SkanDump, Floating }

        private MapKind _kind;
        private Anchors _anchors;

        public string Name => "SONY MTK EMMC";

        public int Priority => 50;

        /// <summary>Опорные точки, найденные плавающим поиском.</summary>
        private struct Anchors
        {
            public long Perm, Persist, FatA, AvbA, AvbB, FatB, FatC, ThirdRw, TailFat, Data;
        }

        public LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, IAnalysisHost host, CancellationToken ct)
        {
            _kind = MapKind.None;

            if (MatchesGn5Ur(dump))
            {
                _kind = MapKind.Gn5Ur;
                host.Log(Strings.Get("Extract_SonyFixedMapGn5ur"), AnalysisLogLevel.Found);
            }
            else if (MatchesSkanDump(dump))
            {
                _kind = MapKind.SkanDump;
                host.Log(Strings.Get("Extract_SonyFixedMapSkan"), AnalysisLogLevel.Found);
            }
            else if (TryFindAnchors(dump, ct, out _anchors))
            {
                _kind = MapKind.Floating;
                host.Log(Strings.Get("Extract_SonyFloatingMap"), AnalysisLogLevel.Found);
            }

            return _kind == MapKind.None ? null : LayoutDetection.At(Name, 0);
        }

        public void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct)
        {
            switch (_kind)
            {
                case MapKind.Gn5Ur:
                    AddFixedMap(table, SonyMaps.Gn5Ur);
                    break;
                case MapKind.SkanDump:
                    AddFixedMap(table, SonyMaps.SkanDump);
                    break;
                case MapKind.Floating:
                    AddFloatingMap(table, dump, _anchors);
                    break;
            }
        }

        private static void AddFixedMap(PartitionTable table, SonyMaps.Entry[] map)
        {
            foreach (var entry in map)
                table.Add(entry.Name, entry.Offset, entry.Length, entry.FsType, "SONY_MTK_EMMC");
        }

        // ---------- точные карты ----------

        private static bool MatchesGn5Ur(IDumpReader dump)
        {
            if (dump.LogicalSize < 0x3A4E00000 || dump.LogicalSize > 0x3C0000000) return false;

            return QuickSignatures.IsExt4(dump, 0x00D00000)
                && QuickSignatures.IsExt4(dump, 0x01800000)
                && QuickSignatures.IsExt4(dump, 0x46B00000)
                && QuickSignatures.IsExt4(dump, 0x297100000)
                && QuickSignatures.IsAvb(dump, 0x0C900000)
                && QuickSignatures.IsAvb(dump, 0x0CA00000)
                && QuickSignatures.IsFat16(dump, 0x07800000)
                && QuickSignatures.IsFat16(dump, 0x0DB00000)
                && QuickSignatures.IsFat16(dump, 0x1DB00000)
                && QuickSignatures.IsFat16(dump, 0x292F00000);
        }

        private static bool MatchesSkanDump(IDumpReader dump)
        {
            if (dump.LogicalSize < 0x3A2C00000 || dump.LogicalSize > 0x3C0000000) return false;

            return QuickSignatures.IsExt4(dump, 0x00D00000)
                && QuickSignatures.IsExt4(dump, 0x01800000)
                && QuickSignatures.IsExt4(dump, 0x46B00000)
                && QuickSignatures.IsExt4(dump, 0x294F00000)
                && QuickSignatures.IsAvb(dump, 0x0C900000)
                && QuickSignatures.IsAvb(dump, 0x0CA00000)
                && QuickSignatures.IsFat16(dump, 0x07800000)
                && QuickSignatures.IsFat16(dump, 0x0DB00000);
        }

        // ---------- плавающий поиск ----------

        /// <summary>
        /// Ищет опорную точку рядом с ожидаемым местом. Поиск идёт по границам в мегабайт:
        /// разделы в таких дампах всегда выровнены, а перебор по секторам занял бы часы.
        /// </summary>
        private static long FindNear(IDumpReader dump, long center, long radius, Func<IDumpReader, long, bool> matches)
        {
            long begin = center > radius ? center - radius : 0;
            return FindFrom(dump, begin, center + radius, matches);
        }

        /// <summary>
        /// Ищет опорную точку строго после заданного места.
        ///
        /// Нужно там, где точка ищется относительно уже найденной: второй заголовок AVB
        /// после первого и раздел данных после хвостового тома FAT16. Оригинал и в этих
        /// двух случаях искал «в окрестности», то есть начинал перебор далеко позади
        /// отправной точки — и первым же находил её саму. Из-за этого второй AVB всегда
        /// совпадал с первым, проверка порядка опорных точек не проходила, и плавающая
        /// карта не строилась ни на одном дампе. Ошибка односторонняя: до неё эта ветка
        /// разбора просто никогда не срабатывала.
        /// </summary>
        private static long FindFrom(IDumpReader dump, long start, long limit, Func<IDumpReader, long, bool> matches)
        {
            long begin = Math.Max(0, start) / AnchorStep * AnchorStep;
            long end = Math.Min(limit, dump.LogicalSize);

            for (long offset = begin; offset < end; offset += AnchorStep)
            {
                if (matches(dump, offset)) return offset;
            }

            return 0;
        }

        private static bool TryFindAnchors(IDumpReader dump, CancellationToken ct, out Anchors anchors)
        {
            anchors = default;

            if (dump.LogicalSize < 0x300000000 || dump.LogicalSize > 0x400000000) return false;

            ct.ThrowIfCancellationRequested();

            anchors.Perm = FindNear(dump, 0x00D00000, 0x00400000, QuickSignatures.IsExt4);
            anchors.Persist = FindNear(dump, 0x01800000, 0x00400000, QuickSignatures.IsExt4);
            anchors.FatA = FindNear(dump, 0x07800000, 0x00800000, QuickSignatures.IsFat16);

            anchors.AvbA = FindNear(dump, 0x0C900000, 0x00800000, QuickSignatures.IsAvb);
            if (anchors.AvbA == 0) return false;

            // Второй AVB ищется строго после первого, иначе им окажется он же.
            anchors.AvbB = FindFrom(dump, anchors.AvbA + 0x100000, anchors.AvbA + 0x100000 + 0x00800000, QuickSignatures.IsAvb);

            anchors.FatB = FindNear(dump, 0x0DB00000, 0x02000000, QuickSignatures.IsFat16);
            anchors.FatC = FindNear(dump, 0x1DB00000, 0x02000000, QuickSignatures.IsFat16);
            anchors.ThirdRw = FindNear(dump, 0x46B00000, 0x08000000, QuickSignatures.IsExt4);

            ct.ThrowIfCancellationRequested();

            anchors.TailFat = FindNear(dump, 0x292F00000, 0x10000000, QuickSignatures.IsFat16);
            if (anchors.TailFat == 0) return false;

            // Раздел данных — тоже строго после хвостового тома FAT16.
            anchors.Data = FindFrom(dump, anchors.TailFat + 0x02000000, anchors.TailFat + 0x02000000 + 0x10000000, QuickSignatures.IsExt4);

            if (anchors.Perm == 0 || anchors.Persist == 0 || anchors.FatA == 0 || anchors.AvbB == 0 ||
                anchors.FatB == 0 || anchors.FatC == 0 || anchors.ThirdRw == 0 || anchors.Data == 0)
                return false;

            // Точки обязаны идти строго по возрастанию: иначе это не эта разметка,
            // а случайные совпадения сигнатур в чужом дампе.
            return anchors.Perm < anchors.Persist
                && anchors.Persist < anchors.FatA
                && anchors.FatA < anchors.AvbA
                && anchors.AvbA < anchors.AvbB
                && anchors.AvbB < anchors.FatB
                && anchors.FatB < anchors.FatC
                && anchors.FatC < anchors.ThirdRw
                && anchors.ThirdRw < anchors.TailFat
                && anchors.TailFat < anchors.Data;
        }

        private static void AddFloatingMap(PartitionTable table, IDumpReader dump, Anchors a)
        {
            void Add(string name, long offset, long length, FsType fs = FsType.Unknown) =>
                table.Add(name, offset, length, fs, "SONY_MTK_EMMC");

            // Промежутки между опорными точками добавляются, только если они непустые:
            // у разных прошивок часть областей отсутствует.
            void AddIfAny(string name, long offset, long length)
            {
                if (length > 0) Add(name, offset, length);
            }

            Add("uboot", 0x00000000, 0x00200000);
            Add("uboot_02", 0x00400000, 0x00200000);
            AddIfAny("part_02", 0x00C00000, a.Perm - 0x00C00000);
            Add("perm", a.Perm, 0x00800000, FsType.Ext4);
            Add("part_06", 0x01500000, 0x00010000);
            AddIfAny("part_07", 0x01700000, a.Persist - 0x01700000);
            Add("persist", a.Persist, a.FatA - a.Persist, FsType.Ext4);
            Add("fat16_09", a.FatA, 0x00400000, FsType.Fat16);
            AddIfAny("part_mid_01", a.FatA + 0x00400000, a.AvbA - (a.FatA + 0x00400000));
            Add("avb_a", a.AvbA, 0x00010000);
            Add("avb_b", a.AvbB, 0x00010000);
            AddIfAny("part_mid_02", a.AvbB + 0x00010000, a.FatB - (a.AvbB + 0x00010000));
            Add("fat16_29", a.FatB, a.FatC - a.FatB, FsType.Fat16);
            Add("fat16_30", a.FatC, a.ThirdRw - a.FatC, FsType.Fat16);
            Add("3rd_rw", a.ThirdRw, 0x10000000, FsType.Ext4);
            AddIfAny("part_large", a.ThirdRw + 0x10000000, a.TailFat - (a.ThirdRw + 0x10000000));
            Add("fat16_tail", a.TailFat, 0x02000000, FsType.Fat16);
            AddIfAny("part_tail_gap", a.TailFat + 0x02000000, a.Data - (a.TailFat + 0x02000000));

            // Размер последнего раздела берётся из суперблока: он единственный, чей
            // размер в плавающей карте не выводится из соседних опорных точек.
            Add("data", a.Data, QuickSignatures.Ext4Size(dump, a.Data), FsType.Ext4);
        }
    }
}
