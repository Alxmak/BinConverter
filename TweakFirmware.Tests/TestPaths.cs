using System.IO;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Полные пути для тестов, работающих с именами файлов.
    ///
    /// Программа рассчитана на Windows, и раньше такие тесты просто записывали
    /// <c>C:\dumps\emmc.bin</c>. На другой системе это не путь, а имя файла целиком:
    /// обратная косая черта там обычный символ, и <see cref="Path.GetFullPath(string)"/>
    /// приписывает такую «строку» к текущей папке, а <see cref="Path.GetExtension(string)"/>
    /// не отделяет папку от имени. Тесты падали не потому, что код неверен, а потому,
    /// что проверяли его на данных, которых на этой системе не бывает.
    ///
    /// Здесь путь собирается по правилам той системы, где тест запущен, поэтому один
    /// и тот же тест проверяет одно и то же поведение и в Windows, и в Linux.
    /// </summary>
    internal static class TestPaths
    {
        /// <summary>
        /// Полный путь из имени тома и сегментов: <c>C:\dumps\emmc.bin</c> в Windows,
        /// <c>/C/dumps/emmc.bin</c> в остальных системах.
        ///
        /// Имя тома остаётся отдельным сегментом и там, где дисков нет: тесты
        /// различают <c>C:</c> и <c>D:</c>, и эта разница должна сохраняться.
        /// </summary>
        public static string Absolute(string drive, params string[] segments) =>
            Path.Combine(Root(drive), Path.Combine(segments));

        /// <summary>Тот же путь, но с разделителем на конце — так пишут папку назначения.</summary>
        public static string Folder(string drive, params string[] segments) =>
            Absolute(drive, segments) + Path.DirectorySeparatorChar;

        private static string Root(string drive) =>
            OperatingSystem.IsWindows() ? drive + @":\" : "/" + drive + "/";
    }
}
