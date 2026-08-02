namespace TweakFirmware.Core
{
    public static class SizeFormatHelper
    {
        private const long OneGb = 1024L * 1024 * 1024;

        /// <summary>
        /// Единый формат размера во всём интерфейсе: "7.15 ГБ (7 680 063 488 байт)"
        /// для файлов от 1 ГБ, "512.34 МБ (537 395 200 байт)" для файлов меньше 1 ГБ.
        /// </summary>
        public static string Format(long bytes)
        {
            if (bytes >= OneGb)
            {
                double gb = bytes / 1024.0 / 1024.0 / 1024.0;
                return $"{gb:F2} ГБ ({bytes:N0} байт)";
            }
            else
            {
                double mb = bytes / 1024.0 / 1024.0;
                return $"{mb:F2} МБ ({bytes:N0} байт)";
            }
        }
    }
}
