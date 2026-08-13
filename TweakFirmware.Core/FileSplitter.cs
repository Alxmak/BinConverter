using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    public class SplitResult
    {
        public string SourceHash { get; set; } = "";
        public string RecombinedHash { get; set; } = "";
        public bool VerifyPerformed { get; set; }
        public bool HashesMatch => string.Equals(SourceHash, RecombinedHash, StringComparison.OrdinalIgnoreCase);
        public int PartsCreated { get; set; } // общее число файлов (включая базовый)
        public long TotalBytes { get; set; }
        public string BasePath { get; set; } = "";
    }

    /// <summary>
    /// Прогресс разбиения: и по операции в целом, и по текущему создаваемому файлу —
    /// достаточно данных, чтобы UI показал два прогресс-бара сразу.
    /// </summary>
    public readonly struct SplitProgress
    {
        public int CurrentFileIndex { get; init; }   // 1-based
        public int TotalFiles { get; init; }
        public string CurrentFileName { get; init; }
        public long CurrentFileBytesWritten { get; init; }
        public long CurrentFileSizeBytes { get; init; }
        public long TotalBytesWritten { get; init; }
        public long TotalBytes { get; init; }
    }

    public static class FileSplitter
    {
        // 4 055 040 КиБ (1 КБ = 1024 байта) = 4 152 360 960 байт — максимум для TNM5000
        public const long DefaultMaxPartSizeBytes = 4_055_040L * 1024L;

        /// <summary>
        /// Сколько файлов получится при разбиении файла заданного размера на части
        /// заданного лимита — используется для предварительного показа в интерфейсе
        /// ДО начала операции.
        /// </summary>
        public static int CalculateExpectedPartCount(long sourceSizeBytes, long maxPartSizeBytes)
        {
            if (maxPartSizeBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPartSizeBytes));
            if (sourceSizeBytes <= 0)
                return 1; // пустой файл — всё равно создаётся один (пустой) базовый файл

            // Округление вверх без сложения: привычное (size + limit - 1) / limit
            // переполняет long на больших размерах и даёт отрицательный результат —
            // именно так проверка «частей слишком много» и не срабатывала.
            long count = sourceSizeBytes / maxPartSizeBytes;
            if (sourceSizeBytes % maxPartSizeBytes != 0) count++;

            // Приведение к int раньше делалось молча. При крошечном лимите на большом файле
            // (терабайтный образ, лимит в килобайт) число частей выходит за int, и молчаливое
            // приведение давало отрицательное значение: интерфейс показывал «-2 147 483 648
            // файлов», а нарезка начиналась и заполняла диск. Такой ввод надо отвергать
            // до начала работы.
            if (count > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maxPartSizeBytes),
                    $"Частей получилось бы {count} — больше, чем программа может создать.");

            return (int)count;
        }

        public static async Task<SplitResult> SplitAsync(
            string sourcePath,
            string outputFolder,
            string baseFileName,
            long maxPartSizeBytes,
            bool verifyHash,
            IProgress<SplitProgress>? progress,
            Action<string> log,
            CancellationToken ct,
            List<string>? createdFilePaths = null,
            IProgress<(long done, long total)>? hashVerifyProgress = null,
            PauseController? pauseController = null)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(Strings.Get("Core_SourceFileNotFound"), sourcePath);
            if (maxPartSizeBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPartSizeBytes));

            var sourceInfo = new FileInfo(sourcePath);
            long totalBytes = sourceInfo.Length;
            int totalFiles = CalculateExpectedPartCount(totalBytes, maxPartSizeBytes);

            Directory.CreateDirectory(outputFolder);
            string basePath = Path.Combine(outputFolder, baseFileName);

            var result = new SplitResult { TotalBytes = totalBytes, BasePath = basePath };

            using var sha256Source = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            const int bufferSize = 4 * 1024 * 1024; // 4 МБ
            byte[] buffer = new byte[bufferSize];

            int partIndex = 0; // 0-based: 0 = базовый файл, 1 = .part1, ...
            string currentPath = basePath;
            long writtenInCurrent = 0;
            long totalWritten = 0;

            // Размер файла с данным 0-based индексом (все, кроме последнего, — ровно maxPartSizeBytes)
            long CurrentFileExpectedSize(int idx) => Math.Min(maxPartSizeBytes, totalBytes - (long)idx * maxPartSizeBytes);

            // ВАЖНО: исходный файл открывается только на чтение (FileAccess.Read) —
            // программа никогда не модифицирует и не удаляет исходный BIN.
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
            {
                // Ссылка обнуляется на время подмены файла (см. ниже) — от этого зависит
                // блок finally: без обнуления он вызывал Flush у уже закрытого потока.
                FileStream? output = new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
                createdFilePaths?.Add(currentPath);
                log(Strings.Format("Log_PartCreated", Path.GetFileName(currentPath), partIndex + 1, totalFiles));

                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (pauseController != null) await pauseController.WaitIfPausedAsync(ct);

                        sha256Source.AppendData(buffer, 0, read);

                        int offset = 0;
                        while (offset < read)
                        {
                            long remaining = maxPartSizeBytes - writtenInCurrent;

                            if (remaining <= 0)
                            {
                                output.Flush();
                                output.Dispose();
                                // Обнуляем до создания следующего файла: если создать его
                                // не выйдет (нет места, имя занято папкой, отобрали права),
                                // finally не должен трогать уже закрытый поток. Раньше он
                                // вызывал у него Flush и подменял настоящую причину сбоя
                                // на ObjectDisposedException — по журналу выходило, что
                                // программа сама у себя закрыла файл.
                                output = null;
                                log(Strings.Format("Log_PartFilled", Path.GetFileName(currentPath), writtenInCurrent));

                                partIndex++;
                                currentPath = $"{basePath}.part{partIndex}";
                                output = new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
                                createdFilePaths?.Add(currentPath);
                                log(Strings.Format("Log_PartCreated", Path.GetFileName(currentPath), partIndex + 1, totalFiles));
                                writtenInCurrent = 0;
                                remaining = maxPartSizeBytes;
                            }

                            int toWrite = (int)Math.Min(remaining, read - offset);
                            await output.WriteAsync(buffer.AsMemory(offset, toWrite), ct);
                            offset += toWrite;
                            writtenInCurrent += toWrite;
                            totalWritten += toWrite;
                        }

                        progress?.Report(new SplitProgress
                        {
                            CurrentFileIndex = partIndex + 1,
                            TotalFiles = totalFiles,
                            CurrentFileName = Path.GetFileName(currentPath),
                            CurrentFileBytesWritten = writtenInCurrent,
                            CurrentFileSizeBytes = CurrentFileExpectedSize(partIndex),
                            TotalBytesWritten = totalWritten,
                            TotalBytes = totalBytes
                        });
                    }
                }
                finally
                {
                    // Отдельная строка именно перед закрытием: на этом шаге Windows
                    // дописывает на диск всё, что ещё лежит в её кэше, и на файле
                    // в несколько гигабайт это занимает заметное время. Антивирус
                    // проверяет только что созданный файл тоже при закрытии, а папка
                    // внутри OneDrive добавляет к этому начало синхронизации. Без этой
                    // строки журнал обрывался на «Создан …», и отличить «работает, но
                    // долго» от «зависло» было нельзя.
                    //
                    // Потока может не быть: сбой мог случиться ровно между закрытием
                    // предыдущей части и созданием следующей.
                    if (output != null)
                    {
                        log(Strings.Format("Log_ClosingFile", Path.GetFileName(currentPath)));
                        output.Flush();
                        output.Dispose();
                    }
                }
            }

            log(Strings.Format("Log_PartFinished", Path.GetFileName(currentPath), writtenInCurrent));

            result.SourceHash = Convert.ToHexString(sha256Source.GetHashAndReset());
            result.PartsCreated = partIndex + 1;

            if (verifyHash)
            {
                log(Strings.Get("Log_VerifyingHash"));
                result.RecombinedHash = await HashHelper.ComputePartsHashAsync(basePath, partIndex, ct, hashVerifyProgress);
                result.VerifyPerformed = true;
                log(result.HashesMatch
                    ? Strings.Get("Log_HashMatches")
                    : Strings.Get("Log_HashMismatchError"));
            }

            return result;
        }
    }
}
