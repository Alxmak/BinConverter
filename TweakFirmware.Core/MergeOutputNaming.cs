using System.IO;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Имя файла, который получится после сборки цепочки. Выводится из базового файла
    /// цепочки: "emmc.bin" -> "emmc_merged.bin". Функция чистая, но с неочевидными
    /// краями — имя без расширения, имя с несколькими точками, уже собранный файл, —
    /// поэтому вынесена сюда и покрыта тестами.
    /// </summary>
    public static class MergeOutputNaming
    {
        /// <summary>Суффикс, по которому собранный файл отличается от исходной цепочки.</summary>
        public const string MergedSuffix = "_merged";

        /// <summary>
        /// Имя итогового файла по базовому файлу цепочки. Ожидает базовый путь
        /// (результат <see cref="HashHelper.ResolveBasePath"/>), а не имя части
        /// вида "emmc.bin.part003".
        /// </summary>
        public static string SuggestFileName(string basePath) =>
            Path.GetFileNameWithoutExtension(basePath) + MergedSuffix + Path.GetExtension(basePath);
    }
}
