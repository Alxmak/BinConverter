using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    public class MergeResult
    {
        public string MergedHash { get; set; } = "";
        public long TotalBytes { get; set; }
        public int PartsUsed { get; set; }
        public string OutputPath { get; set; } = "";
    }

    /// <summary>
    /// Прогресс сборки: по операции в целом и по текущему читаемому файлу.
    /// </summary>
    public readonly struct MergeProgress
    {
        public int CurrentFileIndex { get; init; } // 1-based
        public int TotalFiles { get; init; }
        public string CurrentFileName { get; init; }
        public long CurrentFileBytesRead { get; init; }
        public long CurrentFileSizeBytes { get; init; }
        public long TotalBytesProcessed { get; init; }
        public long TotalBytes { get; init; }
    }

    public static class FileMerger
    {
        private const int BufferSize = 4 * 1024 * 1024;

        public static async Task<MergeResult> MergeAsync(
            string anyChainFilePath,
            string outputPath,
            IProgress<MergeProgress>? progress,
            Action<string> log,
            CancellationToken ct,
            List<string>? createdFilePaths = null,
            PauseController? pauseController = null)
        {
            string basePath = HashHelper.ResolveBasePath(anyChainFilePath);
            var chain = HashHelper.FindPartChain(basePath);
            int totalFiles = chain.Count;

            long totalBytes = 0;
            foreach (var f in chain) totalBytes += new FileInfo(f).Length;

            log(Strings.Format("Log_ChainFound", chain.Count, totalBytes));

            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            long totalWritten = 0;

            // Части (звенья исходной цепочки) открываются только на чтение — они не изменяются.
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                createdFilePaths?.Add(outputPath);
                for (int i = 0; i < chain.Count; i++)
                {
                    string part = chain[i];
                    long partSize = new FileInfo(part).Length;
                    long bytesReadInPart = 0;

                    log(Strings.Format("Log_ReadingPart", Path.GetFileName(part), i + 1, totalFiles));
                    using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (pauseController != null) await pauseController.WaitIfPausedAsync(ct);
                        sha256.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        totalWritten += read;
                        bytesReadInPart += read;

                        progress?.Report(new MergeProgress
                        {
                            CurrentFileIndex = i + 1,
                            TotalFiles = totalFiles,
                            CurrentFileName = Path.GetFileName(part),
                            CurrentFileBytesRead = bytesReadInPart,
                            CurrentFileSizeBytes = partSize,
                            TotalBytesProcessed = totalWritten,
                            TotalBytes = totalBytes
                        });
                    }
                }

                // Перед закрытием — см. пояснение к тому же шагу в FileSplitter.
                log(Strings.Format("Log_ClosingFile", Path.GetFileName(outputPath)));
            }

            log(Strings.Format("Log_MergeFinished", Path.GetFileName(outputPath), totalWritten));

            return new MergeResult
            {
                MergedHash = Convert.ToHexString(sha256.GetHashAndReset()),
                TotalBytes = totalWritten,
                PartsUsed = chain.Count,
                OutputPath = outputPath
            };
        }
    }
}
