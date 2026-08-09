using System;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Partitions.Layouts
{
    /// <summary>
    /// Что детектор нашёл в дампе: где лежит таблица разделов и её содержимое.
    ///
    /// Таблица передаётся вместе с находкой намеренно. В оригинале поиск и разбор были
    /// разными функциями одного класса, а данные между ними шли через общий буфер: одна
    /// оставляла в нём прочитанный блок, вторая рассчитывала его там найти. Такая связь
    /// не видна в сигнатурах и рвётся при малейшей перестановке вызовов — половина
    /// детекторов от неё зависела.
    /// </summary>
    public sealed record LayoutDetection(string MarkName, long TableOffset, byte[] Table)
    {
        /// <summary>Находка без прочитанной таблицы: разметка опознана, данные читаются позже.</summary>
        public static LayoutDetection At(string markName, long offset) =>
            new(markName, offset, Array.Empty<byte>());
    }

    /// <summary>
    /// Один способ разметки дампа: MBR, GPT, таблица MTK, строка mtdparts и так далее.
    ///
    /// Порядок опроса детекторов — часть алгоритма, а не деталь реализации: цепочка
    /// подобрана так, что узкие и надёжные признаки проверяются раньше широких, и первое
    /// же совпадение останавливает перебор. Поэтому приоритет — свойство детектора,
    /// а не позиция в коде.
    /// </summary>
    public interface ILayoutDetector
    {
        /// <summary>Имя разметки в журнале и в отчёте. Не переводится: это термин.</summary>
        string Name { get; }

        /// <summary>Место в цепочке; меньше — раньше.</summary>
        int Priority { get; }

        /// <summary>
        /// Проверяет, размечен ли дамп этим способом. Не должен ничего добавлять в таблицу
        /// разделов и не должен ничего спрашивать: это просто проверка сигнатуры.
        /// </summary>
        LayoutDetection? TryDetect(IDumpReader dump, DumpContext context, CancellationToken ct);

        /// <summary>Читает разделы по уже найденной разметке.</summary>
        void ReadParts(
            LayoutDetection detection,
            PartitionTable table,
            IDumpReader dump,
            DumpContext context,
            IAnalysisHost host,
            CancellationToken ct);
    }
}
