using System;
using System.IO;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    public readonly struct SpaceCheckResult
    {
        public long RequiredBytes { get; init; }
        public long AvailableBytes { get; init; }
        public bool HasEnoughSpace => AvailableBytes >= RequiredBytes;
        public long MissingBytes => HasEnoughSpace ? 0 : RequiredBytes - AvailableBytes;
    }

    public static class DiskSpaceHelper
    {
        public static long GetAvailableFreeBytes(string anyPathOnDrive)
        {
            string fullPath = Path.GetFullPath(anyPathOnDrive);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                throw new IOException(Strings.Format("Core_CannotDetermineDrive", anyPathOnDrive));

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }

        /// <summary>
        /// Проверяет, хватит ли места для данных заданного размера, с запасом (по умолчанию 5%)
        /// на округление кластеров файловой системы.
        /// </summary>
        public static SpaceCheckResult CheckSpace(string outputPath, long dataSizeBytes, double marginFraction = 0.05)
        {
            long required = (long)(dataSizeBytes * (1 + marginFraction));
            long available = GetAvailableFreeBytes(outputPath);
            return new SpaceCheckResult { RequiredBytes = required, AvailableBytes = available };
        }

        public static string FormatBytes(long bytes) => SizeFormatHelper.Format(bytes);
    }
}
