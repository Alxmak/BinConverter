using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core
{
    public sealed class PartitionMergeResult
    {
        public string ResultHash { get; init; } = "";
        public long TotalBytes { get; init; }
        public int PiecesUsed { get; init; }
        public string OutputPath { get; init; } = "";
    }

    /// <summary>
    /// Сборка образа из извлечённых разделов: каждый файл ложится по своему адресу,
    /// промежутки между ними заполняются.
    ///
    /// От <see cref="FileMerger"/> отличается тем, что куски не идут подряд: у каждого
    /// свой адрес, взятый из его имени, и между ними бывают пустоты. Поэтому и хэш
    /// считается по всему, что записано, включая заполнение, — он относится к готовому
    /// файлу, и его можно сверить с хэшем исходного дампа.
    /// </summary>
    public static class PartitionMerger
    {
        private const int BufferSize = 4 * 1024 * 1024;

        public static async Task<PartitionMergeResult> MergeAsync(
            PartitionMergePlan plan,
            string outputPath,
            byte fillByte,
            IProgress<MergeProgress>? progress,
            Action<string> log,
            CancellationToken ct,
            List<string>? createdFilePaths = null,
            PauseController? pause = null)
        {
            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[BufferSize];

            // Заполнитель создаётся только если пустоты есть: у полной папки разделов
            // их не бывает вовсе — извлечение выдаёт и сами промежутки отдельными файлами.
            byte[]? filler = null;

            long written = 0;
            int total = plan.Pieces.Count;

            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                createdFilePaths?.Add(outputPath);

                for (int i = 0; i < total; i++)
                {
                    var piece = plan.Pieces[i];

                    var context = new MergeProgress
                    {
                        CurrentFileIndex = i + 1,
                        TotalFiles = total,
                        CurrentFileName = Path.GetFileName(piece.Path),
                        CurrentFileSizeBytes = piece.Length,
                        TotalBytes = plan.TotalSize
                    };

                    if (piece.Offset > written)
                    {
                        long gap = piece.Offset - written;
                        log(Strings.Format("PartitionMerge_GapFilledLog", $"0x{written:X}", gap));

                        filler ??= CreateFiller(fillByte);
                        written = await WriteFillerAsync(output, sha256, filler, gap, written, context, progress, pause, ct)
                            .ConfigureAwait(false);
                    }

                    log(Strings.Format("Log_ReadingPart", Path.GetFileName(piece.Path), i + 1, total));

                    using var input = new FileStream(
                        piece.Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

                    long readFromPiece = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (pause != null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

                        sha256.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                        written += read;
                        readFromPiece += read;

                        progress?.Report(new MergeProgress
                        {
                            CurrentFileIndex = context.CurrentFileIndex,
                            TotalFiles = context.TotalFiles,
                            CurrentFileName = context.CurrentFileName,
                            CurrentFileBytesRead = readFromPiece,
                            CurrentFileSizeBytes = context.CurrentFileSizeBytes,
                            TotalBytesProcessed = written,
                            TotalBytes = context.TotalBytes
                        });
                    }

                    // Между осмотром папки и записью проходит время, и файл за это время
                    // мог смениться. Дальше он лёг бы в образ не тем размером, сдвинув
                    // всё, что за ним, — а заметили бы это только по несовпавшему хэшу.
                    if (readFromPiece != piece.Length)
                    {
                        throw new IOException(Strings.Format(
                            "Core_MergePieceChanged", Path.GetFileName(piece.Path), piece.Length, readFromPiece));
                    }
                }

                // Отдельная строка перед закрытием — см. пояснение к тому же шагу
                // в FileSplitter: на файле в несколько гигабайт это занимает время.
                log(Strings.Format("Log_ClosingFile", Path.GetFileName(outputPath)));
            }

            log(Strings.Format("Log_MergeFinished", Path.GetFileName(outputPath), written));

            return new PartitionMergeResult
            {
                ResultHash = Convert.ToHexString(sha256.GetHashAndReset()),
                TotalBytes = written,
                PiecesUsed = total,
                OutputPath = outputPath
            };
        }

        private static byte[] CreateFiller(byte value)
        {
            byte[] filler = new byte[BufferSize];
            if (value != 0) Array.Fill(filler, value);
            return filler;
        }

        /// <summary>
        /// Заполняет пустоту. Отчёты о ходе работы идут с именем следующего куска:
        /// пустота — это не файл, своего имени у неё нет, а полоса не должна замирать.
        /// </summary>
        private static async Task<long> WriteFillerAsync(
            FileStream output,
            IncrementalHash hash,
            byte[] filler,
            long count,
            long written,
            MergeProgress context,
            IProgress<MergeProgress>? progress,
            PauseController? pause,
            CancellationToken ct)
        {
            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (pause != null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

                int chunk = (int)Math.Min(count, filler.Length);

                hash.AppendData(filler, 0, chunk);
                await output.WriteAsync(filler.AsMemory(0, chunk), ct).ConfigureAwait(false);

                written += chunk;
                count -= chunk;

                progress?.Report(new MergeProgress
                {
                    CurrentFileIndex = context.CurrentFileIndex,
                    TotalFiles = context.TotalFiles,
                    CurrentFileName = context.CurrentFileName,
                    CurrentFileBytesRead = 0,
                    CurrentFileSizeBytes = context.CurrentFileSizeBytes,
                    TotalBytesProcessed = written,
                    TotalBytes = context.TotalBytes
                });
            }

            return written;
        }
    }
}
