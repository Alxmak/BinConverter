using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Ход сравнения: какой файл читается сейчас и сколько пройдено от общей работы.</summary>
    public readonly struct VerifyProgress
    {
        /// <summary>Номер файла, начиная с единицы.</summary>
        public int FileIndex { get; init; }

        /// <summary>Сколько файлов сравнивается всего.</summary>
        public int FileCount { get; init; }

        public string FileName { get; init; }

        /// <summary>Прочитано по всем файлам вместе.</summary>
        public long TotalBytesProcessed { get; init; }

        /// <summary>Сколько предстоит прочитать всего — сумма размеров всех файлов.</summary>
        public long TotalBytes { get; init; }
    }

    public sealed class VerifyRequest
    {
        /// <summary>Меньше двух файлов сравнивать не с чем.</summary>
        public const int MinFiles = 2;

        /// <summary>
        /// Предел на число файлов. Каждый файл читается целиком, а дампы бывают
        /// многогигабайтные — пять это разумная граница, за которой сравнение
        /// превращается в многочасовое.
        /// </summary>
        public const int MaxFiles = 5;

        public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>Файлы с одинаковым SHA-256.</summary>
    public sealed class HashGroup
    {
        public string Hash { get; init; } = "";

        /// <summary>Пути файлов этой группы, в том порядке, в котором их задал пользователь.</summary>
        public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();

        public int FileCount => FilePaths.Count;
    }

    public enum VerifyStatus
    {
        /// <summary>Задано меньше двух файлов.</summary>
        NotEnoughFiles,

        /// <summary>Какого-то из файлов нет на диске.</summary>
        FileMissing,

        /// <summary>У всех файлов один и тот же SHA-256.</summary>
        AllIdentical,

        /// <summary>Хэши разошлись — групп больше одной.</summary>
        Different,

        /// <summary>Отменено во время работы.</summary>
        Cancelled,

        /// <summary>Упало с ошибкой чтения.</summary>
        Failed
    }

    public sealed class VerifyOutcome
    {
        public VerifyStatus Status { get; init; }

        /// <summary>
        /// Группы одинаковых хэшей, от самой большой к самой маленькой. Смысл именно
        /// в группах, а не в попарных сравнениях: пять файлов дали бы десять пар,
        /// по которым не видно, какой файл выбивается.
        /// </summary>
        public IReadOnlyList<HashGroup> Groups { get; init; } = Array.Empty<HashGroup>();

        /// <summary>Хэши по файлам, в порядке ввода. Пустая строка — не успели посчитать.</summary>
        public IReadOnlyList<string> Hashes { get; init; } = Array.Empty<string>();

        public int FileCount { get; init; }

        /// <summary>Файлы, которых не нашлось на диске.</summary>
        public IReadOnlyList<string> MissingFilePaths { get; init; } = Array.Empty<string>();

        public string ErrorMessage { get; init; } = "";

        public bool HasResult => Status is VerifyStatus.AllIdentical or VerifyStatus.Different;

        /// <summary>Размер самой большой группы — «сколько файлов из всех совпало».</summary>
        public int LargestGroupSize => Groups.Count == 0 ? 0 : Groups[0].FileCount;
    }

    /// <summary>
    /// Сравнение файлов по SHA-256. Файлов может быть от двух до пяти, и результат —
    /// не набор попарных сравнений, а группировка по совпадающему хэшу: сразу видно,
    /// сколько файлов совпало и какой именно выбивается.
    ///
    /// Прогресс сведён к одной шкале на все файлы, чтобы вызывающей стороне не
    /// приходилось самой складывать размеры.
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
            var paths = request.FilePaths;

            if (paths.Count < VerifyRequest.MinFiles)
                return new VerifyOutcome { Status = VerifyStatus.NotEnoughFiles, FileCount = paths.Count };

            var missing = new List<string>();
            foreach (string path in paths)
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) missing.Add(path);

            if (missing.Count > 0)
            {
                return new VerifyOutcome
                {
                    Status = VerifyStatus.FileMissing,
                    FileCount = paths.Count,
                    MissingFilePaths = missing
                };
            }

            // Проверки пройдены — см. пояснение к тому же сигналу в ConvertOperation.
            onStarted?.Invoke();

            log(Strings.Format("Verify_CompareLog", paths.Count));

            var hashes = new string[paths.Count];
            for (int i = 0; i < hashes.Length; i++) hashes[i] = "";

            try
            {
                var sizes = new long[paths.Count];
                long totalWork = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    sizes[i] = new FileInfo(paths[i]).Length;
                    totalWork += sizes[i];
                }

                long processed = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    var relay = Relay(progress, i + 1, paths.Count, Path.GetFileName(paths[i]), processed, totalWork);
                    string path = paths[i];
                    hashes[i] = await Task.Run(() => HashHelper.ComputeFileHashAsync(path, ct, relay));
                    processed += sizes[i];
                }

                var groups = GroupByHash(paths, hashes);
                bool allSame = groups.Count == 1;
                log(Strings.Get(allSame ? "Verify_HashesMatchLog" : "Verify_HashesMismatchLog"));

                return new VerifyOutcome
                {
                    Status = allSame ? VerifyStatus.AllIdentical : VerifyStatus.Different,
                    Groups = groups,
                    Hashes = hashes,
                    FileCount = paths.Count
                };
            }
            catch (OperationCanceledException)
            {
                // Уже посчитанные хэши отдаём: сделанная работа не должна пропадать с экрана.
                return new VerifyOutcome { Status = VerifyStatus.Cancelled, Hashes = hashes, FileCount = paths.Count };
            }
            catch (Exception ex)
            {
                return new VerifyOutcome
                {
                    Status = VerifyStatus.Failed,
                    Hashes = hashes,
                    FileCount = paths.Count,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Собирает файлы в группы по совпадающему хэшу: сначала самая большая группа,
        /// при равном размере — та, чей файл встретился раньше. Порядок важен: по первой
        /// группе интерфейс говорит «N из M файлов идентичны».
        /// </summary>
        public static IReadOnlyList<HashGroup> GroupByHash(IReadOnlyList<string> paths, IReadOnlyList<string> hashes)
        {
            var order = new List<string>();
            var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < paths.Count && i < hashes.Count; i++)
            {
                string hash = hashes[i];
                if (string.IsNullOrEmpty(hash)) continue;

                if (!byHash.TryGetValue(hash, out var files))
                {
                    files = new List<string>();
                    byHash[hash] = files;
                    // Запоминаем написание первого встреченного хэша и его позицию.
                    order.Add(hash);
                }
                files.Add(paths[i]);
            }

            var groups = new List<HashGroup>();
            foreach (string hash in order)
                groups.Add(new HashGroup { Hash = hash, FilePaths = byHash[hash] });

            // Сортировка устойчивая, поэтому при равном размере сохраняется порядок появления.
            groups.Sort((a, b) => b.FileCount.CompareTo(a.FileCount));
            return groups;
        }

        /// <summary>Переводит побайтовый прогресс одного файла в общую шкалу всех.</summary>
        private static IProgress<(long done, long total)>? Relay(
            IProgress<VerifyProgress>? target, int fileIndex, int fileCount, string fileName, long offset, long totalWork) =>
            target == null ? null : new ProgressRelay(target, fileIndex, fileCount, fileName, offset, totalWork);

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
            private readonly int _fileCount;
            private readonly string _fileName;
            private readonly long _offset;
            private readonly long _totalWork;

            public ProgressRelay(IProgress<VerifyProgress> target, int fileIndex, int fileCount, string fileName, long offset, long totalWork)
            {
                _target = target;
                _fileIndex = fileIndex;
                _fileCount = fileCount;
                _fileName = fileName;
                _offset = offset;
                _totalWork = totalWork;
            }

            public void Report((long done, long total) value) => _target.Report(new VerifyProgress
            {
                FileIndex = _fileIndex,
                FileCount = _fileCount,
                FileName = _fileName,
                TotalBytesProcessed = _offset + value.done,
                TotalBytes = _totalWork
            });
        }
    }
}
