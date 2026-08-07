using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что нарезать, куда и с какими проверками.</summary>
    public sealed class ConvertRequest
    {
        /// <summary>Имя, которое подставляется, если пользователь стёр поле.</summary>
        public const string DefaultBaseFileName = "emmc.bin";

        public string SourcePath { get; init; } = "";
        public string OutputFolder { get; init; } = "";
        public string BaseFileName { get; init; } = "";
        public long MaxPartSizeBytes { get; init; }
        public bool VerifyHash { get; init; }
        public bool CheckDiskSpace { get; init; }

        public string EffectiveBaseFileName =>
            string.IsNullOrWhiteSpace(BaseFileName) ? DefaultBaseFileName : BaseFileName.Trim();
    }

    public enum ConvertStatus
    {
        /// <summary>Исходного файла нет — операция не начиналась.</summary>
        SourceNotFound,

        /// <summary>Не задана папка назначения.</summary>
        OutputFolderNotSpecified,

        /// <summary>Лимит на файл нулевой или отрицательный.</summary>
        InvalidPartSize,

        /// <summary>Места на диске назначения не хватает — не начинали, чтобы не упасть на середине.</summary>
        NotEnoughSpace,

        /// <summary>Пользователь отказался от записи при конфликте имён.</summary>
        CancelledBeforeStart,

        /// <summary>Нарезано, проверка хэша не запрашивалась.</summary>
        Completed,

        /// <summary>Нарезано, SHA-256 совпал — байты не потеряны.</summary>
        CompletedVerified,

        /// <summary>Нарезано, но SHA-256 разошёлся: результату доверять нельзя.</summary>
        HashMismatch,

        /// <summary>Отменено во время работы, недописанные файлы удалены.</summary>
        Cancelled,

        /// <summary>Упало с ошибкой, недописанные файлы удалены.</summary>
        Failed
    }

    /// <summary>
    /// Итог операции. Ни диалогов, ни текстов интерфейса — только факты; как их показать,
    /// решает вызывающая сторона. Из-за этого одну и ту же операцию можно запустить
    /// из окна программы (диалоги) и из очереди заданий (запись в список).
    /// </summary>
    public sealed class ConvertOutcome
    {
        public ConvertStatus Status { get; init; }

        /// <summary>Папка, в которую фактически писали: при конфликте могла стать соседней.</summary>
        public string OutputFolder { get; init; } = "";

        /// <summary>Папку пришлось сменить из-за конфликта имён.</summary>
        public bool OutputFolderChanged { get; init; }

        public int PartsCreated { get; init; }
        public long TotalBytes { get; init; }
        public string SourceHash { get; init; } = "";
        public string RecombinedHash { get; init; } = "";

        /// <summary>Размер исходного файла — нужен для сообщения о нехватке места.</summary>
        public long SourceSizeBytes { get; init; }
        public SpaceCheckResult SpaceCheck { get; init; }

        public string ErrorMessage { get; init; } = "";

        /// <summary>Ошибка вызвана нехваткой места, а не чем-то ещё.</summary>
        public bool DiskFull { get; init; }

        /// <summary>Сколько файлов успела создать прерванная операция (все они удалены).</summary>
        public int CreatedFileCount { get; init; }

        /// <summary>Файлы на диске пригодны к использованию.</summary>
        public bool Succeeded => Status is ConvertStatus.Completed or ConvertStatus.CompletedVerified;
    }

    /// <summary>
    /// Нарезка файла на части: проверки, разрешение конфликта имён, запуск, уборка за
    /// сбоем. Раньше всё это жило в ConvertViewModel и было наглухо приварено к WPF —
    /// самый большой метод программы, который писал пользователю многогигабайтные файлы
    /// и при этом не был покрыт ни одним тестом.
    /// </summary>
    public static class ConvertOperation
    {
        public static async Task<ConvertOutcome> RunAsync(
            ConvertRequest request,
            IConflictResolver conflicts,
            IProgress<SplitProgress>? progress,
            IProgress<(long done, long total)>? hashProgress,
            Action<string> log,
            PauseController? pauseController,
            CancellationToken ct,
            Action? onStarted = null,
            // Подменяемая проверка места. Иначе эту ветку нельзя проверить тестом — для неё
            // нужен исходник крупнее свободного места на диске сборочной машины, — а ветка
            // важная: именно она не даёт начать нарезку, которая умрёт на середине
            // и оставит после себя недописанные файлы.
            Func<string, long, SpaceCheckResult>? spaceCheck = null)
        {
            // Порядок проверок важен: сначала то, что можно узнать не трогая диск.
            if (!File.Exists(request.SourcePath))
                return new ConvertOutcome { Status = ConvertStatus.SourceNotFound };

            if (string.IsNullOrWhiteSpace(request.OutputFolder))
                return new ConvertOutcome { Status = ConvertStatus.OutputFolderNotSpecified };

            if (request.MaxPartSizeBytes <= 0)
                return new ConvertOutcome { Status = ConvertStatus.InvalidPartSize };

            string baseName = request.EffectiveBaseFileName;
            string outputFolder = request.OutputFolder;
            bool folderChanged = false;
            long sourceSize = new FileInfo(request.SourcePath).Length;

            if (FileConflictHelper.AnyConflict(outputFolder, baseName))
            {
                var decision = await conflicts.ResolveOutputFolderConflictAsync(outputFolder, baseName);

                if (decision == ConflictDecision.Cancel)
                    return new ConvertOutcome { Status = ConvertStatus.CancelledBeforeStart, OutputFolder = outputFolder };

                if (decision == ConflictDecision.UseAlternative)
                {
                    outputFolder = FileConflictHelper.SuggestAlternativeFolder(outputFolder);
                    folderChanged = true;
                    log(Strings.Format("Convert_ConflictLog", outputFolder));
                }
            }

            // Создаём папку до проверки места: свободное место считается по её диску,
            // а у несуществующего пути диск определить не всегда возможно.
            Directory.CreateDirectory(outputFolder);

            if (request.CheckDiskSpace)
            {
                var check = spaceCheck is null
                    ? DiskSpaceHelper.CheckSpace(outputFolder, sourceSize)
                    : spaceCheck(outputFolder, sourceSize);
                if (!check.HasEnoughSpace)
                {
                    return new ConvertOutcome
                    {
                        Status = ConvertStatus.NotEnoughSpace,
                        OutputFolder = outputFolder,
                        OutputFolderChanged = folderChanged,
                        SourceSizeBytes = sourceSize,
                        SpaceCheck = check
                    };
                }
            }

            // Все проверки пройдены и работа сейчас начнётся. Отдельный сигнал нужен потому,
            // что отказ на любой из проверок выше не должен выглядеть как начатая операция:
            // интерфейс не гасит поля, а очередь не считает задание запущенным.
            onStarted?.Invoke();

            log(Strings.Format("Convert_StartLog", request.SourcePath, outputFolder, baseName, request.MaxPartSizeBytes));

            // Список пополняется по ходу работы и нужен именно на случай срыва: по нему
            // удаляются недописанные файлы.
            var createdFiles = new List<string>();

            // Фоновый режим процесса ставится на всю операцию: он общий для процесса,
            // поэтому вкладывать такие области друг в друга нельзя — очередь должна
            // выполнять задания последовательно.
            using var backgroundIo = new BackgroundIoScope();

            try
            {
                // Без передачи токена в Task.Run: отмена должна дойти до самой нарезки
                // и отработать штатно, а не превратиться в исключение до её начала.
                var result = await Task.Run(() => FileSplitter.SplitAsync(
                    request.SourcePath, outputFolder, baseName, request.MaxPartSizeBytes, request.VerifyHash,
                    progress, log, ct, createdFiles, hashProgress, pauseController));

                log(Strings.Format("Convert_FinishedLog", result.PartsCreated, result.TotalBytes));

                return new ConvertOutcome
                {
                    Status = !result.VerifyPerformed
                        ? ConvertStatus.Completed
                        : result.HashesMatch ? ConvertStatus.CompletedVerified : ConvertStatus.HashMismatch,
                    OutputFolder = outputFolder,
                    OutputFolderChanged = folderChanged,
                    PartsCreated = result.PartsCreated,
                    TotalBytes = result.TotalBytes,
                    SourceHash = result.SourceHash,
                    RecombinedHash = result.RecombinedHash,
                    SourceSizeBytes = sourceSize,
                    CreatedFileCount = createdFiles.Count
                };
            }
            catch (OperationCanceledException)
            {
                log(Strings.Get("Convert_CancelledLog"));
                IncompleteOutput.Remove(createdFiles, log);

                return new ConvertOutcome
                {
                    Status = ConvertStatus.Cancelled,
                    OutputFolder = outputFolder,
                    OutputFolderChanged = folderChanged,
                    CreatedFileCount = createdFiles.Count
                };
            }
            catch (Exception ex)
            {
                log(Strings.Format("Convert_ErrorLog", ex.Message));
                IncompleteOutput.Remove(createdFiles, log);

                return new ConvertOutcome
                {
                    Status = ConvertStatus.Failed,
                    OutputFolder = outputFolder,
                    OutputFolderChanged = folderChanged,
                    ErrorMessage = ex.Message,
                    DiskFull = IncompleteOutput.IsDiskFull(ex),
                    CreatedFileCount = createdFiles.Count
                };
            }
        }
    }
}
