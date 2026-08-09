using System;
using System.Collections.Generic;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>Том файловой системы, найденный в дампе.</summary>
    public sealed record VolumeInfo(FsType Type, string Name, long Offset, long Length)
    {
        /// <summary>Примечание к разделу — например, о том, что том вылез за его границы.</summary>
        public string Comment { get; init; } = "";

        /// <summary>Строки для журнала: размер блока, свободное место, метка тома и прочее.</summary>
        public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Проверка «начинается ли по этому адресу том такой-то файловой системы».
    ///
    /// Зонды опрашиваются по очереди на каждом шаге сканирования дампа, поэтому у них
    /// два требования. Первое — быть придирчивыми: пропущенное ложное срабатывание
    /// превращается в несуществующий раздел, который потом попадёт и в таблицу, и в
    /// извлечённые файлы. Второе — быть дешёвыми: на большом дампе зонд вызывается
    /// миллионы раз, и лишнее чтение обходится дорого.
    /// </summary>
    public interface IFileSystemProbe
    {
        FsType Type { get; }

        /// <summary>
        /// Возвращает описание тома или <c>null</c>. <paramref name="partitionEnd"/> —
        /// граница раздела, внутри которого идёт поиск: если том по своему заголовку
        /// вылезает за неё, размер обрезается, а в примечание попадает предупреждение.
        /// </summary>
        VolumeInfo? TryProbe(IDumpReader dump, long offset, long partitionEnd);
    }

    /// <summary>Мелочи, общие для всех зондов.</summary>
    internal static class ProbeHelpers
    {
        /// <summary>
        /// Размер тома округляется вверх до четырёх килобайт. Так делал оригинал для всех
        /// образов, которые пишутся целиком: на носителе они всегда занимают целое число
        /// блоков, а в заголовке хранится размер полезных данных.
        /// </summary>
        public const long BlockAlignment = 0x1000;

        public static long AlignUp(long value, long alignment)
        {
            if (alignment <= 0) return value;
            long remainder = value % alignment;
            return remainder == 0 ? value : value - remainder + alignment;
        }

        /// <summary>
        /// Обрезает том по границе раздела и объясняет это в примечании. Заголовок может
        /// врать — в дампе с повреждённой прошивкой это обычное дело.
        /// </summary>
        public static VolumeInfo ClampToPartition(VolumeInfo volume, long partitionEnd)
        {
            if (partitionEnd <= 0 || volume.Offset + volume.Length <= partitionEnd) return volume;

            long clamped = partitionEnd - volume.Offset;
            if (clamped <= 0) return volume;

            return volume with
            {
                Length = clamped,
                Comment = Localization.Strings.Format(
                    "Extract_VolumeExceedsPartition", $"0x{volume.Length:X}", $"0x{clamped:X}")
            };
        }
    }
}
