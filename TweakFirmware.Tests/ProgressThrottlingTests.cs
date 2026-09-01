using System.IO;
using System.Threading;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.FileSystems;
using TweakFirmware.Core.Partitions;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Насколько часто проходы по дампу сообщают о ходе работы.
    ///
    /// Это не про экономию тактов. Каждое сообщение о прогрессе — отдельная работа,
    /// поставленная в очередь потока интерфейса, и два изменения свойств с уведомлениями.
    /// Два прохода сообщали о себе на каждом шаге: поиск файловых систем — на каждые
    /// четыре килобайта (миллион сообщений на дампе в четыре гигабайта), определение
    /// геометрии NAND — на каждые шестнадцать (полмиллиона на восьми гигабайтах). Поток
    /// интерфейса захлёбывался: окно не отвечало всё время разбора, а полоса прогресса
    /// отставала от настоящей работы.
    ///
    /// Проверяем не «быстро ли», а именно частоту: она задана в коде числом, и число
    /// это легко потерять при следующей правке цикла.
    /// </summary>
    public class ProgressThrottlingTests
    {
        [Fact]
        public void Scan_ReportsProgressOnceEveryFewHundredSteps()
        {
            // Пустая область: ни один зонд ничего не найдёт, поэтому проход идёт до конца
            // ровным шагом в 0x1000 — то есть ровно 512 шагов на двух мегабайтах.
            const long region = 0x200000;
            const long steps = region / 0x1000;

            using var dump = new PlainDumpReader(new MemoryStream(new DumpBuilder(region).Build()));
            var host = new RecordingAnalysisHost();

            int found = FileSystemScanner.Scan(dump, 0, region, new PartitionTable(), host, CancellationToken.None);

            Assert.Equal(0, found);

            // Прореживание — раз в 256 шагов, считая первый: 512 шагов дают ровно два
            // сообщения. Без него их было бы 512 — сравнение с числом шагов и есть суть
            // проверки, само число 2 без него ничего не значит.
            Assert.Equal(2, host.Reports.Count);
            Assert.True(host.Reports.Count < steps / 100);
        }

        [Fact]
        public void TryDetectPageSize_ReportsProgressOnceEveryFewHundredBlocks()
        {
            // Сплошные данные без служебных областей: геометрия не опознается, и проход
            // идёт по всему файлу — тот самый случай, ради которого прореживание и нужно.
            // Блок здесь 16384 + 16384/32 = 16896 байт, кладём ровно восемь блоков.
            const int block = 16384 + 16384 / 32;
            const int blocks = 8;

            var flat = new DumpBuilder(block * blocks).Pattern(0, block * blocks).Build();
            using var stream = new MemoryStream(flat);
            var host = new RecordingAnalysisHost();

            var geometry = NandGeometryDetector.TryDetectPageSize(stream, 0x20, 0x01, host.Progress);

            Assert.Null(geometry);

            // Восемь блоков — одно сообщение (первое). Без прореживания их было бы восемь.
            Assert.Single(host.Reports);
        }
    }
}
