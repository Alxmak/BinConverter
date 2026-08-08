using System.IO;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Во что превращать то, что бросили на строку папки. Проводник отдаёт список путей
    /// без указания, папки это или файлы, а прицелиться мышью в саму папку труднее, чем
    /// схватить лежащий в ней файл, — поэтому файл принимаем тоже и берём его папку.
    /// </summary>
    public static class DroppedFolder
    {
        /// <summary>
        /// Папка, которую имел в виду пользователь: сам путь, если это папка; папка файла,
        /// если бросили файл; <c>null</c>, если по этому пути ничего нет — подставлять
        /// выдуманный путь в поле хуже, чем не реагировать.
        /// </summary>
        public static string? Resolve(string? droppedPath)
        {
            if (string.IsNullOrWhiteSpace(droppedPath)) return null;

            if (Directory.Exists(droppedPath)) return droppedPath;

            if (File.Exists(droppedPath))
            {
                string? folder = Path.GetDirectoryName(droppedPath);
                return string.IsNullOrEmpty(folder) ? null : folder;
            }

            return null;
        }
    }
}
