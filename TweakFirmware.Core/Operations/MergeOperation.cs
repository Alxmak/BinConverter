using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что собирать, куда и с чем сверить результат.</summary>
    public sealed class MergeRequest
    {
        /// <summary>Любой файл цепочки — остальные находятся по нему.</summary>
        public string AnyChainFilePath { get; init; } = "";

        public string OutputPath { get; init; } = "";

        /// <summary>Проверять ли свободное место перед началом. Та же галочка есть
        /// в Конвертировании — это одна и та же проверка.</summary>
        public bool CheckDiskSpace { get; init; }

        /// <summary>Необязательный ожидаемый SHA-256 собранного файла.</summary>
        public string ExpectedHash { get; init; } = "";

        public bool HasExpectedHash => !Sha256Text.IsBlank(ExpectedHash);

        /// <summary>
        /// Заполненное поле, но не похожее на SHA-256. Отделено от <see cref="HasExpectedHash"/>,
        /// потому что реакция разная: пустое поле — законный случай (сверять не просили),
        /// а мусор в поле надо показать до начала работы, а не после.
        /// </summary>
        public bool HasMalformedExpectedHash => HasExpectedHash && !Sha256Text.LooksValid(ExpectedHash);
    }

    public enum MergeStatus
    {
        /// <summary>Указанного файла цепочки нет.</summary>
        SourceNotFound,

        /// <summary>Не задан файл результата.</summary>
        OutputPathNotSpecified,

        /// <summary>Поле «Ожидаемый SHA-256» заполнено, но это не хэш.</summary>
        ExpectedHashInvalid,

        /// <summary>Файл результата — это один из файлов собираемой цепочки.</summary>
        OutputInsideChain,

        /// <summary>
        /// С папкой результата работать нельзя: путь набран с ошибкой, диска нет,
        /// прав нет, или у сетевого ресурса не удалось спросить свободное место.
        /// </summary>
        OutputNotUsable,

        /// <summary>Цепочку частей собрать не удалось — пропуск, нечитаемый файл и подобное.</summary>
        ChainResolveFailed,

        /// <summary>Места на диске назначения не хватает.</summary>
        NotEnoughSpace,

        /// <summary>Пользователь отказался перезаписывать существующий файл.</summary>
        CancelledBeforeStart,

        /// <summary>Собрано, ожидаемый хэш не задавался — сверять было не с чем.</summary>
        Completed,

        /// <summary>Собрано, SHA-256 совпал с ожидаемым.</summary>
        HashMatch,

        /// <summary>Собрано, но SHA-256 не совпал с ожидаемым.</summary>
        HashMismatch,

        /// <summary>Отменено во время работы, недописанный файл удалён.</summary>
        Cancelled,

        /// <summary>Упало с ошибкой, недописанный файл удалён.</summary>
        Failed
    }

    public sealed class MergeOutcome
    {
        public MergeStatus Status { get; init; }

        /// <summary>Путь, по которому фактически писали: при конфликте мог стать соседним.</summary>
        public string OutputPath { get; init; } = "";

        /// <summary>Путь пришлось сменить из-за существующего файла.</summary>
        public bool OutputPathChanged { get; init; }

        public int PartsUsed { get; init; }
        public long TotalBytes { get; init; }
        public string MergedHash { get; init; } = "";

        /// <summary>Суммарный размер цепочки — нужен для сообщения о нехватке места.</summary>
        public long ChainSizeBytes { get; init; }
        public SpaceCheckResult SpaceCheck { get; init; }

        public string ErrorMessage { get; init; } = "";
        public bool DiskFull { get; init; }
        public int CreatedFileCount { get; init; }

        public bool Succeeded => Status is MergeStatus.Completed or MergeStatus.HashMatch;
    }

    /// <summary>
    /// Сборка цепочки частей в один файл. Раньше жила в MergeViewModel вместе с диалогами.
    /// </summary>
    public static class MergeOperation
    {
        /// <summary>
        /// Суммарный размер цепочки по любому её файлу. Отдельно от самой сборки, потому что
        /// это же число нужно интерфейсу до запуска — показать, какой файл получится.
        /// </summary>
        public static long GetChainSize(string anyChainFilePath, out List<string> chain)
        {
            string basePath = HashHelper.ResolveBasePath(anyChainFilePath);
            chain = HashHelper.FindPartChain(basePath);

            long total = 0;
            foreach (string part in chain) total += new FileInfo(part).Length;
            return total;
        }

        public static async Task<MergeOutcome> RunAsync(
            MergeRequest request,
            IConflictResolver conflicts,
            IProgress<MergeProgress>? progress,
            Action<string> log,
            PauseController? pauseController,
            CancellationToken ct,
            Action? onStarted = null,
            // Подменяемая проверка места — см. пояснение в ConvertOperation.
            Func<string, long, SpaceCheckResult>? spaceCheck = null)
        {
            if (!File.Exists(request.AnyChainFilePath))
                return new MergeOutcome { Status = MergeStatus.SourceNotFound };

            if (string.IsNullOrWhiteSpace(request.OutputPath))
                return new MergeOutcome { Status = MergeStatus.OutputPathNotSpecified };

            // До конфликтов и до первого байта: сборка идёт часами, и узнавать об опечатке
            // в конце — значит потратить их зря.
            if (request.HasMalformedExpectedHash)
                return new MergeOutcome { Status = MergeStatus.ExpectedHashInvalid };

            string outputPath = request.OutputPath;
            bool pathChanged = false;

            // Цепочка разбирается раньше вопроса о перезаписи: если она вообще нечитаема,
            // спрашивать про перезапись незачем. И только зная её состав, можно проверить,
            // что результат — не один из её же файлов.
            long chainSize;
            List<string> chain;
            try
            {
                chainSize = GetChainSize(request.AnyChainFilePath, out chain);
            }
            catch (Exception ex)
            {
                return new MergeOutcome
                {
                    Status = MergeStatus.ChainResolveFailed,
                    OutputPath = outputPath,
                    ErrorMessage = ex.Message
                };
            }

            // Самая дорогая ошибка из возможных: FileMerger создаёт файл результата до
            // чтения частей, поэтому запись «в саму цепочку» обнулила бы одну из частей,
            // а уборка за сорванной операцией её потом удалила. См. OutputCollision.
            if (OutputCollision.ChainIncludes(chain, outputPath))
                return new MergeOutcome { Status = MergeStatus.OutputInsideChain, OutputPath = outputPath };

            if (File.Exists(outputPath))
            {
                var decision = await conflicts.ResolveOutputFileConflictAsync(outputPath);

                if (decision == ConflictDecision.Cancel)
                    return new MergeOutcome { Status = MergeStatus.CancelledBeforeStart, OutputPath = outputPath };

                if (decision == ConflictDecision.UseAlternative)
                {
                    outputPath = FileConflictHelper.SuggestAlternativeFilePath(outputPath);
                    pathChanged = true;
                    log(Strings.Format("Merge_ConflictLog", outputPath));
                }
            }

            // Как и в Конвертировании: работа с папкой назначения может упасть по причинам,
            // к сборке не относящимся (нет диска, нет прав, сетевой ресурс), и раньше это
            // исключение улетало наружу и роняло приложение.
            try
            {
                string outputFolder = Path.GetDirectoryName(outputPath) ?? ".";
                Directory.CreateDirectory(outputFolder);

                // Проверку можно отключить — та же галочка и та же форма записи, что
                // в Конвертировании: раньше здесь она была безусловной, хотя это одна
                // и та же проверка на обеих вкладках.
                if (request.CheckDiskSpace)
                {
                    var check = spaceCheck is null
                        ? DiskSpaceHelper.CheckSpace(outputFolder, chainSize)
                        : spaceCheck(outputFolder, chainSize);
                    if (!check.HasEnoughSpace)
                    {
                        return new MergeOutcome
                        {
                            Status = MergeStatus.NotEnoughSpace,
                            OutputPath = outputPath,
                            OutputPathChanged = pathChanged,
                            ChainSizeBytes = chainSize,
                            SpaceCheck = check
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return new MergeOutcome
                {
                    Status = MergeStatus.OutputNotUsable,
                    OutputPath = outputPath,
                    OutputPathChanged = pathChanged,
                    ErrorMessage = ex.Message
                };
            }

            // Проверки пройдены — см. пояснение к тому же сигналу в ConvertOperation.
            onStarted?.Invoke();

            log(Strings.Format("Merge_StartLog", request.AnyChainFilePath, outputPath));

            var createdFiles = new List<string>();
            try
            {
                var result = await Task.Run(() => FileMerger.MergeAsync(
                    request.AnyChainFilePath, outputPath, progress, log, ct, createdFiles, pauseController));

                log(Strings.Format("Merge_FinishedLog", result.PartsUsed, result.TotalBytes));
                log(Strings.Format("Merge_ResultHashLog", result.MergedHash));

                MergeStatus status = MergeStatus.Completed;
                if (request.HasExpectedHash)
                {
                    // Normalize, а не Trim: хэш, скопированный из нашего же окна с итогом,
                    // приходит разбитым на строки — там он переносится, чтобы влезть.
                    bool match = string.Equals(
                        Sha256Text.Normalize(request.ExpectedHash), result.MergedHash, StringComparison.OrdinalIgnoreCase);
                    log(match ? Strings.Get("Merge_HashMatchLog") : Strings.Get("Merge_HashMismatchLog"));
                    status = match ? MergeStatus.HashMatch : MergeStatus.HashMismatch;
                }

                return new MergeOutcome
                {
                    Status = status,
                    OutputPath = result.OutputPath,
                    OutputPathChanged = pathChanged,
                    PartsUsed = result.PartsUsed,
                    TotalBytes = result.TotalBytes,
                    MergedHash = result.MergedHash,
                    ChainSizeBytes = chainSize,
                    CreatedFileCount = createdFiles.Count
                };
            }
            catch (OperationCanceledException)
            {
                log(Strings.Get("Merge_CancelledLog"));
                IncompleteOutput.Remove(createdFiles, log);

                return new MergeOutcome
                {
                    Status = MergeStatus.Cancelled,
                    OutputPath = outputPath,
                    OutputPathChanged = pathChanged,
                    CreatedFileCount = createdFiles.Count
                };
            }
            catch (Exception ex)
            {
                log(Strings.Format("Common_ErrorLog", ex.Message));
                IncompleteOutput.Remove(createdFiles, log);

                return new MergeOutcome
                {
                    Status = MergeStatus.Failed,
                    OutputPath = outputPath,
                    OutputPathChanged = pathChanged,
                    ErrorMessage = ex.Message,
                    DiskFull = IncompleteOutput.IsDiskFull(ex),
                    CreatedFileCount = createdFiles.Count
                };
            }
        }
    }
}
