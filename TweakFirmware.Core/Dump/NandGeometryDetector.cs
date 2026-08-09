using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Dump
{
    /// <summary>
    /// Определение геометрии страницы NAND по самому дампу — без чтения ID микросхемы.
    ///
    /// Задача нетривиальная: в файле нет ни заголовка, ни метки, только данные вперемешку
    /// со служебными вставками. Решается в два шага, и оба целиком перенесены из
    /// оригинального скрипта вместе с эмпирическими порогами: они подобраны автором по
    /// реальным дампам телевизоров, вывести их из теории нельзя.
    ///
    /// Шаг первый даёт соотношение main:spare — из одного лишь размера файла. Шаг второй
    /// находит абсолютный размер страницы по статистике байт 0xFF: в служебных областях
    /// стёртой микросхемы их много, в данных — мало.
    /// </summary>
    public static class NandGeometryDetector
    {
        /// <summary>
        /// Ниже этого размера дамп даже не проверяем. Порог из оригинала: 64 МиБ.
        /// Микросхем NAND меньшего объёма в технике, ради которой всё писалось, не бывает,
        /// а вот дампов eMMC и NOR подходящего размера — сколько угодно.
        /// </summary>
        public const long MinimumNandDumpSize = 0x4000000;

        /// <summary>Наибольший размер main, который перебирается на втором шаге.</summary>
        private const int MaxMain = 16384;

        /// <summary>Наименьший размер main.</summary>
        private const int MinMain = 256;

        /// <summary>Сколько вариантов main перебирается: 256, 512, … 16384.</summary>
        private const int MainVariants = 7;

        /// <summary>
        /// Размер дампа микросхемы K9GAG08U0E. У неё соотношение не выводится общим
        /// правилом, поэтому оно прописано отдельно — так же, как в оригинале.
        /// </summary>
        private const long K9Gag08U0ESize = 0x88A7D800;

        /// <summary>
        /// Шаг 1. Соотношение main:spare из размера файла.
        ///
        /// Идея в том, что размер дампа NAND — это (число страниц) × (main + spare), где
        /// число страниц всегда степень двойки. Значит, если отбросить у размера все
        /// младшие нули, останется само значение (main + spare), сокращённое до
        /// наименьших целых. Их-то и сопоставляем с таблицей известных микросхем.
        /// </summary>
        public static (int RelativeMain, int RelativeSpare)? TryDetectRatio(long fileSize)
        {
            if (fileSize <= 0) return null;

            if (fileSize == K9Gag08U0ESize) return (0x800, 0x6D);

            long value = fileSize;
            do
            {
                value >>= 1;
            }
            while ((value & 1) == 0);

            return value switch
            {
                0x011 => (0x010, 0x01),   // 2048 + 128
                0x021 => (0x020, 0x01),   // 2048 + 64
                0x045 => (0x040, 0x05),   // 4096 + 320
                0x087 => (0x080, 0x07),   // 4096 + 224
                0x10F => (0x100, 0x0F),   // 8192 + 480
                _ => null
            };
        }

        /// <summary>
        /// Шаг 2. Абсолютный размер страницы по статистике байт 0xFF.
        ///
        /// Дамп просматривается блоками размером с самую большую возможную страницу.
        /// Годится блок, где байт 0xFF достаточно много, чтобы там были служебные области,
        /// но недостаточно, чтобы блок оказался просто стёртым. На таком блоке
        /// перебираются все семь вариантов main, и для каждого считается, сколько 0xFF
        /// попало бы в предполагаемые области spare. Побеждает вариант с максимумом.
        /// </summary>
        public static NandGeometry? TryDetectPageSize(
            Stream file,
            int relativeMain,
            int relativeSpare,
            IProgress<AnalysisProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (relativeMain <= 0 || relativeSpare <= 0) return null;

            int maxSpare = MaxMain / relativeMain * relativeSpare;
            if (maxSpare <= 0) return null;

            int maxPage = MaxMain + maxSpare;
            var block = new byte[maxPage];
            long size = file.Length;

            for (long offset = 0; offset + maxPage <= size; offset += maxPage)
            {
                ct.ThrowIfCancellationRequested();

                file.Position = offset;
                if (!ReadExactly(file, block)) break;

                int countFf = 0;
                foreach (byte b in block)
                    if (b == 0xFF) countFf++;

                // Порог из оригинала. Верхняя граница отсекает стёртые блоки, нижняя —
                // блоки со сплошными данными, где spare не разглядеть.
                if (countFf <= maxSpare && countFf > maxSpare / 16)
                {
                    var geometry = AnalysePage(block, relativeMain, relativeSpare, maxSpare);
                    if (geometry is not null) return geometry;
                }

                progress?.Report(new AnalysisProgress(Strings.Get("Extract_StageNandGeometry"), offset, size));
            }

            return null;
        }

        private static NandGeometry? AnalysePage(ReadOnlySpan<byte> block, int relativeMain, int relativeSpare, int maxSpare)
        {
            Span<int> counts = stackalloc int[MainVariants];

            for (int i = 0; i < MainVariants; i++)
                counts[i] = CountFfInSpareZones(block, MinMain << i, relativeMain, relativeSpare);

            int best = counts[0];
            int bestIndex = 0;
            for (int i = 1; i < MainVariants; i++)
            {
                if (counts[i] > best)
                {
                    best = counts[i];
                    bestIndex = i;
                }
            }

            // Вариант main = 16384 оригинал считает заведомо ложным срабатыванием.
            if (bestIndex == MainVariants - 1) return null;

            // Второй эмпирический порог: байт 0xFF в spare должно набраться хотя бы
            // около одной восьмой от его размера, иначе это шум, а не служебная область.
            if (best < maxSpare / 8) return null;

            int main = MinMain << bestIndex;
            int spare = main / relativeMain * relativeSpare;
            if (spare <= 0) return null;

            return new NandGeometry(main, spare, relativeMain, relativeSpare);
        }

        /// <summary>
        /// Сколько байт 0xFF окажется в областях spare, если размер main равен заданному.
        /// Блок просматривается «страницами» предполагаемого размера, и в каждой считается
        /// только хвост — то, что при такой гипотезе было бы служебной областью.
        /// </summary>
        private static int CountFfInSpareZones(ReadOnlySpan<byte> block, int main, int relativeMain, int relativeSpare)
        {
            int spare = main / relativeMain * relativeSpare;
            if (spare <= 0) return 0;

            int count = 0;
            for (int offset = 0; offset < block.Length; offset += main + spare)
            {
                int start = offset + main;
                int end = Math.Min(start + spare, block.Length);
                for (int i = start; i < end; i++)
                    if (block[i] == 0xFF) count++;
            }

            return count;
        }

        /// <summary>
        /// Варианты для ручного выбора, когда автоопределение не справилось. Последний в
        /// списке — «spare нет»: им пользователь говорит разбирать дамп как сплошной.
        ///
        /// Каждый spare считается от своего main, а не удвоением предыдущего, как в
        /// оригинале. Для всех соотношений, кроме одного, это одно и то же, но у
        /// K9GAG08U0E (relativeMain = 0x800) первый вариант даёт 256/2048 = 0, и дальше
        /// оригинал удваивал ноль — то есть все восемь вариантов оказывались без spare.
        /// </summary>
        public static IReadOnlyList<NandGeometryOption> BuildManualOptions(int relativeMain, int relativeSpare)
        {
            var options = new List<NandGeometryOption>();

            int index = 0;
            for (int main = MinMain; main <= 32768; main *= 2)
                options.Add(new NandGeometryOption(index++, main, main / relativeMain * relativeSpare));

            options.Add(new NandGeometryOption(index, 0, 0));
            return options;
        }

        /// <summary>Геометрия по выбранному пользователем варианту; <c>null</c> — «spare нет».</summary>
        public static NandGeometry? FromManualChoice(int index, int relativeMain, int relativeSpare)
        {
            var options = BuildManualOptions(relativeMain, relativeSpare);
            if (index < 0 || index >= options.Count) return null;

            var option = options[index];
            return option.IsNoSpare ? null : new NandGeometry(option.Main, option.Spare, relativeMain, relativeSpare);
        }

        /// <summary>
        /// Раскладка Dune HD опознаётся по метке в первых четырёх байтах страницы:
        /// FF FF AA FF у TV-102 и AA AA AA AA у TV-301. Порог — восемь совпадений
        /// из первых десяти страниц.
        /// </summary>
        public static DuneHdVariant DetectDuneHd(Stream file, int pageSize)
        {
            const uint Marker102 = 0xFFAAFFFF;
            const uint Marker301 = 0xAAAAAAAA;
            const int PagesToCheck = 10;
            const int Threshold = 8;

            if (pageSize <= 0) return DuneHdVariant.None;

            var head = new byte[4];
            int count102 = 0, count301 = 0;

            for (int i = 0; i < PagesToCheck; i++)
            {
                long position = (long)i * pageSize;
                if (position + head.Length > file.Length) break;

                file.Position = position;
                if (!ReadExactly(file, head)) break;

                uint signature = BinaryPrimitives.ReadUInt32LittleEndian(head);
                if (signature == Marker102) count102++;
                if (signature == Marker301) count301++;
            }

            if (count301 >= Threshold) return DuneHdVariant.Tv301;
            if (count102 >= Threshold) return DuneHdVariant.Tv102;
            return DuneHdVariant.None;
        }

        /// <summary>
        /// Полный разбор геометрии: оба шага, при неудаче — вопрос пользователю, при удаче —
        /// дополнительная проверка на раскладку Dune HD. Возвращает <c>null</c>, если дамп
        /// следует читать как сплошной.
        /// </summary>
        public static async Task<NandGeometry?> DetectAsync(
            Stream file,
            IAnalysisHost host,
            CancellationToken ct = default)
        {
            if (file.Length <= MinimumNandDumpSize) return null;

            var ratio = TryDetectRatio(file.Length);
            if (ratio is null) return null;

            host.Log(Strings.Get("Extract_NandDumpDetected"), AnalysisLogLevel.Found);
            host.Log(Strings.Get("Extract_NandSearchingGeometry"));

            var geometry = TryDetectPageSize(file, ratio.Value.RelativeMain, ratio.Value.RelativeSpare, host.Progress, ct);

            if (geometry is null)
            {
                host.Log(Strings.Get("Extract_NandGeometryUnknown"), AnalysisLogLevel.Warning);

                var options = BuildManualOptions(ratio.Value.RelativeMain, ratio.Value.RelativeSpare);
                int? choice = await host.AskNandGeometryAsync(options, ct).ConfigureAwait(false);
                if (choice is null) return null;

                geometry = FromManualChoice(choice.Value, ratio.Value.RelativeMain, ratio.Value.RelativeSpare);
                if (geometry is null) return null;
            }
            else
            {
                var dune = DetectDuneHd(file, geometry.Page);
                if (dune != DuneHdVariant.None)
                {
                    host.Log(Strings.Get("Extract_DuneHdLayoutFound"), AnalysisLogLevel.Found);
                    geometry = geometry with { Layout = NandPageLayout.DuneHd, DuneVariant = dune };
                }
            }

            host.Log(Strings.Format("Extract_NandGeometryFound", geometry.Main, geometry.Spare), AnalysisLogLevel.Found);
            return geometry;
        }

        private static bool ReadExactly(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer[total..]);
                if (read <= 0) return false;
                total += read;
            }
            return true;
        }
    }
}
