using System;
using System.Collections.Generic;
using System.IO;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Не пишем ли мы поверх того, что читаем.
    ///
    /// Для сборки это вопрос сохранности данных, а не удобства: <see cref="FileMerger"/>
    /// открывает файл результата на запись (FileMode.Create) до того, как начнёт читать
    /// части. Если результат — это один из файлов цепочки, он обнуляется первым же
    /// действием, дальше чтение той же части падает, а уборка за сорванной операцией
    /// его удаляет. Часть прошивки исчезает, и восстановить её нечем.
    ///
    /// Для нарезки последствия мягче: исходник открыт на чтение с FileShare.Read, поэтому
    /// Windows сама не даст открыть его на запись — но операция умрёт посреди работы
    /// с невнятной ошибкой ввода-вывода. Сказать это заранее и понятными словами лучше.
    ///
    /// Сравнение путей — по полному пути и без учёта регистра: в Windows "C:\A\x.bin",
    /// "c:\a\X.BIN" и "C:\A\..\A\x.bin" — один и тот же файл.
    /// </summary>
    public static class OutputCollision
    {
        /// <summary>
        /// Есть ли путь результата среди файлов цепочки. Пути, которые не удалось привести
        /// к полному виду, считаются несовпадающими: разбираться с некорректным путём —
        /// дело проверок, идущих отдельно, а не этой.
        /// </summary>
        public static bool ChainIncludes(IReadOnlyList<string> chain, string outputPath)
        {
            string? target = TryNormalize(outputPath);
            if (target == null) return false;

            foreach (string part in chain)
                if (string.Equals(TryNormalize(part), target, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        /// Попадёт ли исходный файл нарезки под один из создаваемых файлов. Нарезка пишет
        /// baseFileName и baseFileName.part1 … .part(N-1) в папку вывода, поэтому совпадение
        /// возможно и когда имена на первый взгляд разные: исходник "emmc.bin.part1"
        /// в той же папке будет перезаписан второй создаваемой частью.
        /// </summary>
        public static bool SplitWouldOverwriteSource(
            string sourcePath, string outputFolder, string baseFileName, int partCount)
        {
            string? source = TryNormalize(sourcePath);
            if (source == null) return false;

            string? basePath = TryNormalize(Path.Combine(outputFolder, baseFileName));
            if (basePath == null) return false;

            if (string.Equals(basePath, source, StringComparison.OrdinalIgnoreCase)) return true;

            for (int i = 1; i < partCount; i++)
                if (string.Equals($"{basePath}.part{i}", source, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private static string? TryNormalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }
    }
}
