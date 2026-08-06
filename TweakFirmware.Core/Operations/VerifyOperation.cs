using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Ход сравнения: какой файл читается сейчас и сколько пройдено от общей работы.</summary>
    public readonly struct VerifyProgress
    {
        /// <summary>1 — первый файл, 2 — второй.</summary>
        public int FileIndex { get; init; }

        public string FileName { get; init; }

        /// <summary>Прочитано по обоим файлам вместе.</summary>
        public long TotalBytesProcessed { get; init; }

        /// <summary>Сколько предстоит прочитать всего — сумма размеров обоих файлов.</summary>
        public long TotalBytes { get; init; }
    }

    public sealed class VerifyRequest
    {
        public string FileAPath { get; init; } = "";
        public string FileBPath { get; init; } = "";
    }

    public enum VerifyStatus
    {
        /// <summary>Одного из файлов нет — сравнивать нечего.</summary>
        FileMissing,

        /// <summary>Хэши совпали: файлы побайтово одинаковые.</summary>
        Match,

        /// <summary>Хэши разошлись: файлы различаются.</summary>
        Mismatch,

        /// <summary>Отменено во время работы.</summary>
        Cancelled,

        /// <summary>Упало с ошибкой чтения.</summary>
        Failed
    }

    public sealed class VerifyOutcome
    {
        public VerifyStatus Status { get; init; }

        /// <summary>Хэш первого файла, если его успели досчитать.</summary>
        public string HashA { get; init; } = "";

        /// <summary>Хэш второго файла, если его успели досчитать.</summary>
        public string HashB { get; init; } = "";

        public string ErrorMessage { get; init; } = "";

        public bool HasResult => Status is VerifyStatus.Match or VerifyStatus.Mismatch;
    }

    /// <summary>
    /// Сравнение двух файлов по SHA-256. Прогресс сведён к одной шкале на оба файла,
    /// чтобы вызывающей стороне не приходилось самой складывать размеры, — и чтобы
    /// очередь заданий могла показывать один общий процент.
    /// </summary>
    public static class VerifyOperation
    {
        public static async Task<VerifyOutcome> RunAsync(
            VerifyRequest request,
            IProgress<VerifyProgress>? progress,
            Action<string> log,
            CancellationToken ct,
            Action? onStarted = null)
        {
            if (!File.Exists(request.FileAPath) || !File.Exists(request.FileBPath))
                return new VerifyOutcome { Status = VerifyStatus.FileMissing };

            // Проверки пройдены — см. пояснение к тому же сигналу в ConvertOperation.
            onStarted?.Invoke();

            log(Strings.Format("Verify_CompareLog", request.FileAPath, request.FileBPath));

            string hashA = "";
            string hashB = "";

            try
            {
                long sizeA = new FileInfo(request.FileAPath).Length;
                long sizeB = new FileInfo(request.FileBPath).Length;
                long totalWork = sizeA + sizeB;

                var progressA = Relay(progress, 1, Path.GetFileName(request.FileAPath), offset: 0, totalWork);
                // Второй файл продолжает ту же шкалу, поэтому его прогресс сдвинут
                // на размер первого.
                var progressB = Relay(progress, 2, Path.GetFileName(request.FileBPath), offset: sizeA, totalWork);

                hashA = await Task.Run(() => HashHelper.ComputeFileHashAsync(request.FileAPath, ct, progressA));
                hashB = await Task.Run(() => HashHelper.ComputeFileHashAsync(request.FileBPath, ct, progressB));

                bool match = string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
                log(match ? Strings.Get("Verify_HashesMatchLog") : Strings.Get("Verify_HashesMismatchLog"));

                return new VerifyOutcome
                {
                    Status = match ? VerifyStatus.Match : VerifyStatus.Mismatch,
                    HashA = hashA,
                    HashB = hashB
                };
            }
            catch (OperationCanceledException)
            {
                // Хэш первого файла мог успеть досчитаться — отдаём то, что есть,
                // чтобы уже сделанная работа не пропала с экрана.
                return new VerifyOutcome { Status = VerifyStatus.Cancelled, HashA = hashA, HashB = hashB };
            }
            catch (Exception ex)
            {
                return new VerifyOutcome
                {
                    Status = VerifyStatus.Failed,
                    HashA = hashA,
                    HashB = hashB,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>Переводит побайтовый прогресс одного файла в общую шкалу обоих.</summary>
        private static IProgress<(long done, long total)>? Relay(
            IProgress<VerifyProgress>? target, int fileIndex, string fileName, long offset, long totalWork) =>
            target == null ? null : new ProgressRelay(target, fileIndex, fileName, offset, totalWork);

        /// <summary>
        /// Пересчитывает и передаёт дальше сразу, в том же потоке. Намеренно не
        /// <see cref="Progress{T}"/>: тот доставляет вызовы через пул потоков, а лишний
        /// переход означает и перестановку отчётов местами, и невоспроизводимые тесты —
        /// на этом мы уже один раз поймали плавающий тест отмены.
        /// </summary>
        private sealed class ProgressRelay : IProgress<(long done, long total)>
        {
            private readonly IProgress<VerifyProgress> _target;
            private readonly int _fileIndex;
            private readonly string _fileName;
            private readonly long _offset;
            private readonly long _totalWork;

            public ProgressRelay(IProgress<VerifyProgress> target, int fileIndex, string fileName, long offset, long totalWork)
            {
                _target = target;
                _fileIndex = fileIndex;
                _fileName = fileName;
                _offset = offset;
                _totalWork = totalWork;
            }

            public void Report((long done, long total) value) => _target.Report(new VerifyProgress
            {
                FileIndex = _fileIndex,
                FileName = _fileName,
                TotalBytesProcessed = _offset + value.done,
                TotalBytes = _totalWork
            });
        }
    }
}
