using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TweakFirmware.Core
{
    public static class HashHelper
    {
        private const int BufferSize = 4 * 1024 * 1024;

        public static async Task<string> ComputeFileHashAsync(
            string path, CancellationToken ct = default,
            IProgress<(long done, long total)>? progress = null)
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            long total = new FileInfo(path).Length;
            long done = 0;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha256.AppendData(buffer, 0, read);
                done += read;
                progress?.Report((done, total));
            }

            return Convert.ToHexString(sha256.GetHashAndReset());
        }

        /// <summary>
        /// Считает SHA-256 по цепочке файлов basePath, basePath.part1, basePath.part2 ... подряд,
        /// не создавая объединённый файл на диске (виртуальная склейка "на лету").
        /// progress — необязательный отчёт (done, total) в байтах, для отдельного прогресс-бара проверки.
        /// </summary>
        public static async Task<string> ComputePartsHashAsync(
            string basePath, int lastPartIndex, CancellationToken ct = default,
            IProgress<(long done, long total)>? progress = null)
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];

            long total = 0;
            for (int i = 0; i <= lastPartIndex; i++)
            {
                string p = i == 0 ? basePath : $"{basePath}.part{i}";
                total += new FileInfo(p).Length;
            }

            long done = 0;
            for (int i = 0; i <= lastPartIndex; i++)
            {
                string path = i == 0 ? basePath : $"{basePath}.part{i}";
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha256.AppendData(buffer, 0, read);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            return Convert.ToHexString(sha256.GetHashAndReset());
        }

        /// <summary>
        /// По любому файлу цепочки (например emmc.bin.part3) находит базовый путь (emmc.bin).
        /// Если это уже базовый файл — возвращает его как есть.
        /// </summary>
        public static string ResolveBasePath(string anyPartPath)
        {
            string name = Path.GetFileName(anyPartPath);
            string dir = Path.GetDirectoryName(anyPartPath) ?? "";
            var match = Regex.Match(name, @"^(.*)\.part\d+$");
            return match.Success ? Path.Combine(dir, match.Groups[1].Value) : anyPartPath;
        }

        /// <summary>
        /// Находит полную цепочку файлов начиная с базового: [basePath, basePath.part1, basePath.part2, ...]
        /// Останавливается на первом отсутствующем номере.
        /// </summary>
        public static List<string> FindPartChain(string basePath)
        {
            var list = new List<string>();
            if (!File.Exists(basePath))
                throw new FileNotFoundException("Базовый файл цепочки не найден", basePath);

            list.Add(basePath);
            int i = 1;
            while (true)
            {
                string p = $"{basePath}.part{i}";
                if (!File.Exists(p)) break;
                list.Add(p);
                i++;
            }
            return list;
        }
    }
}
