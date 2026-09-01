using System;
using System.Collections.Generic;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>
    /// Поиск томов файловых систем внутри дампа.
    ///
    /// Работает вторым проходом, после того как найдена разметка: по каждому разделу
    /// проходят все зонды, и если внутри что-то нашлось, раздел заменяется найденными
    /// томами. Смысл в том, что таблица разделов говорит только границы, а что именно
    /// внутри — приходится выяснять по содержимому. Иногда разметки нет вовсе, и тогда
    /// сканируется весь дамп целиком.
    /// </summary>
    public static class FileSystemScanner
    {
        /// <summary>
        /// Порядок опроса зондов. Менять нельзя: он подобран так, что строгие проверки
        /// идут раньше нестрогих. Например, tar опознаётся только по контрольной сумме
        /// заголовка и потому проверяется последним.
        /// </summary>
        public static IReadOnlyList<IFileSystemProbe> DefaultProbes { get; } = new IFileSystemProbe[]
        {
            new Ext4Probe(),
            new SquashFsProbe(),
            new RomFsProbe(),
            new FatVolumeProbe(),
            new VdfsProbe(),
            new CramFsProbe(),
            new TarProbe()
        };

        /// <summary>Крупный шаг сканирования — для областей, выровненных по четырём килобайтам.</summary>
        private const long CoarseStep = 0x1000;

        /// <summary>Мелкий шаг — сектор.</summary>
        private const long FineStep = 0x200;

        /// <summary>
        /// Через сколько шагов сканирования сообщать о ходе работы.
        ///
        /// Сообщать на каждом шаге нельзя, и это не про экономию тактов. Шаг здесь —
        /// четыре килобайта, то есть на дампе в четыре гигабайта шагов миллион, а каждое
        /// сообщение о прогрессе — это отдельная работа, поставленная в очередь потока
        /// интерфейса (Progress&lt;T&gt;), и два изменения свойств с уведомлениями. Поток
        /// интерфейса захлёбывался в них: окно переставало отвечать на всё время разбора,
        /// а полоса прогресса отставала от настоящей работы. Остальные проходы по дампу
        /// прорежены точно так же — от 0x3F у перебора кандидатов до 0xFFF у разделения
        /// NAND; здесь этого не было.
        ///
        /// Степень двойки, потому что проверяется битовой маской.
        /// </summary>
        private const long ProgressEveryNSteps = 0x100;

        /// <summary>
        /// Ищет тома в заданной области. Найденное добавляется в <paramref name="table"/>;
        /// возвращает, сколько томов нашлось.
        /// </summary>
        public static int Scan(
            IDumpReader dump,
            long offset,
            long length,
            PartitionTable table,
            IAnalysisHost host,
            CancellationToken ct,
            IReadOnlyList<IFileSystemProbe>? probes = null)
        {
            probes ??= DefaultProbes;

            long end = Math.Min(offset + length, dump.LogicalSize);
            if (offset < 0 || end <= offset) return 0;

            // Шаг зависит от того, выровнено ли начало области. Оригинал брал его из
            // переменных, оставшихся от предыдущего вызова, — здесь он считается от того,
            // что сканируется сейчас.
            long step = offset % CoarseStep == 0 ? CoarseStep : FineStep;

            int found = 0;
            long at = offset;

            // Подпись этапа одна на весь проход — незачем собирать её заново на каждом шаге.
            string stage = Strings.Get("Extract_StageScanFs");
            long steps = 0;

            while (at < end)
            {
                ct.ThrowIfCancellationRequested();

                if ((steps++ & (ProgressEveryNSteps - 1)) == 0)
                    host.Progress?.Report(new AnalysisProgress(stage, at - offset, end - offset));

                var volume = Probe(dump, at, end, probes);
                if (volume is null)
                {
                    at += step;
                    continue;
                }

                foreach (string line in volume.Details)
                    if (line.Length > 0) host.Log(line, AnalysisLogLevel.Found);

                table.Add(volume.Name, volume.Offset, volume.Length, volume.Type, volume.Comment);
                found++;

                // Дальше искать имеет смысл сразу за найденным томом.
                long advance = ProbeHelpers.AlignUp(volume.Length, FineStep);
                at = volume.Offset + Math.Max(advance, step);
            }

            return found;
        }

        private static VolumeInfo? Probe(IDumpReader dump, long at, long end, IReadOnlyList<IFileSystemProbe> probes)
        {
            foreach (var probe in probes)
            {
                var volume = probe.TryProbe(dump, at, end);
                if (volume is not null && volume.Length > 0) return volume;
            }

            return null;
        }

        /// <summary>
        /// Второй проход разбора: пройти по уже найденным разделам и заменить те, внутри
        /// которых нашлись тома.
        ///
        /// Раздел, где что-то нашлось, из списка убирается — вместо него остаются тома.
        /// Если у первого тома нет собственного имени, он наследует имя раздела: у
        /// SquashFS, например, имени в заголовке нет вовсе, а имя раздела осмысленное.
        /// </summary>
        public static void ScanPartitions(
            IDumpReader dump,
            PartitionTable table,
            IAnalysisHost host,
            CancellationToken ct,
            IReadOnlyList<IFileSystemProbe>? probes = null)
        {
            var sources = new List<PartitionEntry>(table.Items);
            var result = new List<PartitionEntry>();

            foreach (var part in sources)
            {
                ct.ThrowIfCancellationRequested();

                // Записи самой разметки сканировать незачем: это не разделы с данными.
                if (part.Name.StartsWith("MBR", StringComparison.Ordinal) ||
                    part.Name.StartsWith("EBR", StringComparison.Ordinal) ||
                    part.Offset >= dump.LogicalSize)
                {
                    result.Add(part);
                    continue;
                }

                var inside = new PartitionTable();
                int found = Scan(dump, part.Offset, part.Length, inside, host, ct, probes);

                if (found == 0)
                {
                    result.Add(part);
                    continue;
                }

                var volumes = new List<PartitionEntry>(inside.Items);
                if (volumes[0].Name == "part") volumes[0].Name = part.Name;

                result.AddRange(volumes);
            }

            table.Replace(result);
        }
    }
}
