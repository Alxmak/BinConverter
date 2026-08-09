using System.Linq;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Разметка Sony MediaTek eMMC. Таблицы разделов в таком дампе нет вовсе, границы
    /// восстанавливаются по опорным точкам — поэтому и проверяется здесь именно это:
    /// точные карты узнаются по контрольным точкам, плавающая — по найденным сигнатурам
    /// и их порядку.
    /// </summary>
    public class SonyMtkEmmcDetectorTests
    {
        private const long Gn5UrSize = 0x3A5000000;
        private const long SkanSize = 0x3A3000000;

        private static SparseDumpReader Gn5UrDump()
        {
            var dump = new SparseDumpReader(Gn5UrSize);

            dump.PutExt4(0x00D00000).PutExt4(0x01800000).PutExt4(0x46B00000).PutExt4(0x297100000);
            dump.PutAvb(0x0C900000).PutAvb(0x0CA00000);
            dump.PutFat16(0x07800000).PutFat16(0x0DB00000).PutFat16(0x1DB00000).PutFat16(0x292F00000);

            return dump;
        }

        private static SparseDumpReader SkanDump()
        {
            var dump = new SparseDumpReader(SkanSize);

            dump.PutExt4(0x00D00000).PutExt4(0x01800000).PutExt4(0x46B00000).PutExt4(0x294F00000);
            dump.PutAvb(0x0C900000).PutAvb(0x0CA00000);
            dump.PutFat16(0x07800000).PutFat16(0x0DB00000);

            return dump;
        }

        private static (PartitionTable Table, bool Detected, SilentAnalysisHost Host) Run(IDumpReader dump)
        {
            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();
            var detector = new SonyMtkEmmcDetector();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            if (detection is null) return (table, false, host);

            detector.ReadParts(detection, table, dump, context, host, CancellationToken.None);
            return (table, true, host);
        }

        [Fact]
        public void RecognisesTheGn5UrMapByItsCheckPoints()
        {
            var (table, detected, _) = Run(Gn5UrDump());

            Assert.True(detected);
            Assert.Equal(SonyMaps.Gn5Ur.Length, table.Count);

            Assert.Equal("uboot", table[0].Name);
            Assert.Equal("data", table[table.Count - 1].Name);
            Assert.Equal(0x297100000, table[table.Count - 1].Offset);
            Assert.Equal(FsType.Ext4, table[table.Count - 1].FsType);
        }

        [Fact]
        public void RecognisesTheSkanDumpMap()
        {
            var (table, detected, _) = Run(SkanDump());

            Assert.True(detected);
            Assert.Equal(SonyMaps.SkanDump.Length, table.Count);
            Assert.Equal(0x294F00000, table[table.Count - 1].Offset);
        }

        [Fact]
        public void OneMissingCheckPointRejectsTheFixedMap()
        {
            // Ровно то, ради чего контрольных точек десять: дамп похожего размера
            // с частичным совпадением не должен получить чужую разметку.
            var dump = new SparseDumpReader(Gn5UrSize);
            dump.PutExt4(0x00D00000).PutExt4(0x01800000).PutExt4(0x46B00000).PutExt4(0x297100000);
            dump.PutAvb(0x0C900000).PutAvb(0x0CA00000);
            dump.PutFat16(0x07800000).PutFat16(0x0DB00000).PutFat16(0x1DB00000);
            // Последней точки FAT16 нет.

            Assert.False(Run(dump).Detected);
        }

        [Fact]
        public void SizeOutsideTheRangeIsNotEvenChecked()
        {
            var dump = new SparseDumpReader(0x100000000);   // 4 ГиБ вместо четырнадцати
            dump.PutExt4(0x00D00000).PutExt4(0x01800000);

            Assert.False(Run(dump).Detected);
        }

        [Fact]
        public void FallsBackToTheFloatingMapWhenTheFixedOnesDoNotFit()
        {
            // Опорные точки сдвинуты относительно точных карт — но лежат в тех же
            // окрестностях и в правильном порядке.
            var dump = new SparseDumpReader(0x380000000);

            dump.PutExt4(0x00E00000);                        // perm
            dump.PutExt4(0x01900000);                        // persist
            dump.PutFat16(0x07900000);                       // fat16_09
            dump.PutAvb(0x0C900000).PutAvb(0x0CB00000);      // avb_a, avb_b
            dump.PutFat16(0x0DC00000);                       // fat16_29
            dump.PutFat16(0x1DC00000);                       // fat16_30
            dump.PutExt4(0x46C00000);                        // 3rd_rw
            dump.PutFat16(0x293000000);                      // fat16_tail
            dump.PutExt4(0x295100000, blockCount: 0x40000);  // data

            var (table, detected, host) = Run(dump);

            Assert.True(detected);

            var perm = table.Single(p => p.Name == "perm");
            Assert.Equal(0x00E00000, perm.Offset);

            var persist = table.Single(p => p.Name == "persist");
            Assert.Equal(0x01900000, persist.Offset);
            Assert.Equal(0x07900000 - 0x01900000, persist.Length);

            // Размер последнего раздела считается из суперблока: 0x40000 блоков по 4 КиБ.
            var data = table.Single(p => p.Name == "data");
            Assert.Equal(0x295100000, data.Offset);
            Assert.Equal(0x40000L * 4096, data.Length);

            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Found);
        }

        [Fact]
        public void FloatingMapNeedsTheAnchorsInTheRightOrder()
        {
            // Все сигнатуры на месте, но хвостовой FAT16 оказался позади ext4 данных —
            // значит, это не эта разметка, а случайные совпадения.
            var dump = new SparseDumpReader(0x380000000);

            dump.PutExt4(0x00E00000);
            dump.PutExt4(0x01900000);
            dump.PutFat16(0x07900000);
            dump.PutAvb(0x0C900000).PutAvb(0x0CB00000);
            dump.PutFat16(0x0DC00000);
            dump.PutFat16(0x1DC00000);
            dump.PutExt4(0x46C00000);
            dump.PutFat16(0x293000000);
            // Раздела данных за хвостовым FAT16 нет вовсе.

            Assert.False(Run(dump).Detected);
        }

        [Fact]
        public void FixedMapsAreOrderedAndDoNotOverlap()
        {
            // Карты — это выписанные вручную данные, и ошибка в одной строке проявилась бы
            // наложением разделов при извлечении.
            foreach (var map in new[] { SonyMaps.Gn5Ur, SonyMaps.SkanDump })
            {
                for (int i = 1; i < map.Length; i++)
                {
                    Assert.True(map[i].Offset > map[i - 1].Offset,
                        $"Смещения должны возрастать: {map[i - 1].Name} → {map[i].Name}");

                    Assert.True(map[i - 1].Offset + map[i - 1].Length <= map[i].Offset,
                        $"Разделы не должны перекрываться: {map[i - 1].Name} → {map[i].Name}");
                }

                Assert.All(map, entry => Assert.True(entry.Length > 0));
            }
        }

        [Fact]
        public void Ext4Size_FallsBackToTheEndOfTheDumpOnNonsense()
        {
            // Логарифм размера блока больше восьми означает мусор в суперблоке.
            var dump = new SparseDumpReader(0x10000000);
            dump.PutExt4(0x1000, blockCount: 0x100, logBlockSize: 99);

            Assert.Equal(0x10000000 - 0x1000, QuickSignatures.Ext4Size(dump, 0x1000));
        }
    }
}
