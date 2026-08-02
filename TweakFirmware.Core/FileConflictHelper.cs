using System.IO;

namespace TweakFirmware.Core
{
    public static class FileConflictHelper
    {
        /// <summary>
        /// Проверяет, есть ли уже в папке файл с именем baseFileName или его частей (.partN).
        /// </summary>
        public static bool AnyConflict(string outputFolder, string baseFileName)
        {
            if (!Directory.Exists(outputFolder)) return false;

            string basePath = Path.Combine(outputFolder, baseFileName);
            if (File.Exists(basePath)) return true;

            return Directory.GetFiles(outputFolder, baseFileName + ".part*").Length > 0;
        }

        /// <summary>
        /// Предлагает свободную от конфликтов папку рядом с указанной: output -> output_2 -> output_3 ...
        /// </summary>
        public static string SuggestAlternativeFolder(string outputFolder)
        {
            string trimmed = outputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(trimmed);
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(parent)) parent = trimmed;

            int i = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(parent, $"{name}_{i}");
                i++;
            }
            while (Directory.Exists(candidate) && Directory.GetFileSystemEntries(candidate).Length > 0);

            return candidate;
        }

        /// <summary>
        /// Предлагает свободное от конфликтов имя файла рядом с указанным: result.bin -> result_2.bin -> ...
        /// </summary>
        public static string SuggestAlternativeFilePath(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath);

            int i = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}_{i}{ext}");
                i++;
            }
            while (File.Exists(candidate));

            return candidate;
        }
    }
}
