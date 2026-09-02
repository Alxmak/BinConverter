using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Partitions;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что складывать обратно и куда.</summary>
    public sealed class PartitionMergeRequest
    {
        /// <summary>Папка с извлечёнными разделами.</summary>
        public string SourceFolder { get; init; } = "";

        public string OutputPath { get; init; } = "";

        /// <summary>
        /// Чем заполнять пустоты. По умолчанию 0xFF — так выглядит стёртая флеш-память;
        /// нули нужны там, где в оригинале незаписанные области нулевые (обычно eMMC).
        /// </summary>
        public bool FillWithZeros { get; init; }

        /// <summary>Проверять ли свободное место перед началом. Та же галочка есть
        /// в Конвертировании и Сборке — это одна и та же проверка.</summary>
        public bool CheckDiskSpace { get; init; }

        public byte FillByte => FillWithZeros ? (byte)0x00 : (byte)0xFF;
    }

    public enum PartitionMergeStatus
    {
        /// <summary>Указанной папки нет.</summary>
        SourceFolderNotFound,

        /// <summary>Папку не удалось прочитать: нет прав, отвалился сетевой диск.</summary>
        SourceFolderNotUsable,

        /// <summary>Не задан файл результата.</summary>
        OutputPathNotSpecified,

        /// <summary>В папке нет ни одного файла, похожего на извлечённый раздел.</summary>
        NothingToMerge,

        /// <summary>Куски не складываются: файл не того размера или два куска на одних байтах.</summary>
        LayoutBroken,

        /// <summary>Файл результата — один из собираемых разделов.</summary>
        OutputInsideSource,

        /// <summary>С папкой результата работать нельзя.</summary>
        OutputNotUsable,

        /// <summary>Места на диске назначения не хватает.</summary>
        NotEnoughSpace,

        /// <summary>Пользователь отказался перезаписывать существующий файл.</summary>
        CancelledBeforeStart,

        /// <summary>Собрано.</summary>
        Completed,

        /// <summary>Отменено во время работы, недописанный файл удалён.</summary>
        Cancelled,

        /// <summary>Упало с ошибкой, недописанный файл удалён.</summary>
        Failed
    }

    public sealed class PartitionMergeOutcome
    {
        public PartitionMergeStatus Status { get; init; }

        public string OutputPath { get; init; } = "";

        /// <summary>Путь пришлось сменить из-за существующего файла.</summary>
        public bool OutputPathChanged { get; init; }

        /// <summary>
        /// План, по которому собирали (или по которому отказались собирать). Нужен
        /// вызывающей стороне, чтобы объяснить отказ: какие файлы не сошлись размером,
        /// какие перекрылись, что вообще нашлось в папке.
        /// </summary>
        public PartitionMergePlan Plan { get; init; } = new();

        public int PiecesUsed { get; init; }
        public long TotalBytes { get; init; }
        public string ResultHash { get; init; } = "";

        public SpaceCheckResult SpaceCheck { get; init; }

        public string ErrorMessage { get; init; } = "";
        public bool DiskFull { get; init; }
        public int CreatedFileCount { get; init; }

        public bool Succeeded => Status == PartitionMergeStatus.Completed;
    }

    /// <summary>
    /// Обратная сборка дампа из извлечённых разделов.
    ///
    /// Действие, обратное извлечению: там дамп резался по таблице разделов на файлы,
    /// здесь файлы складываются обратно в один образ. Ничего угадывать не приходится —
    /// адрес и размер каждого куска записаны в его имени (см. <see cref="PartitionMergePlan"/>).
    ///
    /// Все проверки идут до первого записанного байта: собранный не из того образ
    /// выглядит настоящим, а узнать о подмене можно будет только по хэшу — если было
    /// с чем сверять.
    ///
    /// Метод обязан оставаться в потоке того, кто его позвал: он спрашивает про
    /// перезапись существующего файла и зовёт <c>onStarted</c>, а это окно и свойства
    /// интерфейса. Отсюда правило для всех await'ов ниже — никакого
    /// <c>ConfigureAwait(false)</c>; чем это кончается, написано у первого из них.
    /// </summary>
    public static class PartitionMergeOperation
    {
        public static async Task<PartitionMergeOutcome> RunAsync(
            PartitionMergeRequest request,
            IConflictResolver conflicts,
            IProgress<MergeProgress>? progress,
            Action<string> log,
            PauseController? pauseController,
            CancellationToken ct,
            Action? onStarted = null,
            // Подменяемая проверка места — см. пояснение в ConvertOperation.
            Func<string, long, SpaceCheckResult>? spaceCheck = null)
        {
            if (string.IsNullOrWhiteSpace(request.SourceFolder) || !Directory.Exists(request.SourceFolder))
                return new PartitionMergeOutcome { Status = PartitionMergeStatus.SourceFolderNotFound };

            if (string.IsNullOrWhiteSpace(request.OutputPath))
                return new PartitionMergeOutcome { Status = PartitionMergeStatus.OutputPathNotSpecified };

            // Папку осматриваем заново, а не берём план, посчитанный интерфейсом для показа:
            // между показом и нажатием кнопки в папке могло всё измениться, а собирать
            // надо то, что там лежит сейчас.
            PartitionMergePlan plan;
            try
            {
                // Без передачи токена в Task.Run: с уже отменённым токеном работа даже
                // не началась бы, а наружу ушло бы исключение отмены — и отказ выглядел бы
                // как нечитаемая папка. Осмотр короткий, отмена дождётся записи.
                //
                // И без ConfigureAwait(false). Пока он здесь стоял, всё, что идёт после
                // этой строки, выполнялось в потоке пула: вопрос о перезаписи пытался
                // построить окно, а onStarted — тронуть команды вкладки, и оба падали
                // с «вызывающий поток не может получить доступ к данному объекту».
                // Падало это посреди MarkStarted: занятость успевала встать, а finally
                // снять её уже не мог — и кнопка «Начать» оставалась живой на вид
                // и мёртвой на нажатие до перезапуска программы.
                plan = await Task.Run(() => PartitionMergePlan.Build(request.SourceFolder));
            }
            catch (Exception ex)
            {
                return new PartitionMergeOutcome
                {
                    Status = PartitionMergeStatus.SourceFolderNotUsable,
                    ErrorMessage = ex.Message
                };
            }

            if (plan.Pieces.Count == 0)
                return new PartitionMergeOutcome { Status = PartitionMergeStatus.NothingToMerge, Plan = plan };

            if (plan.Issues.Count > 0)
                return new PartitionMergeOutcome { Status = PartitionMergeStatus.LayoutBroken, Plan = plan };

            string outputPath = request.OutputPath;
            bool pathChanged = false;

            // Та же беда, что и у сборки цепочки: файл результата создаётся до чтения
            // кусков, и если он сам оказался одним из них — кусок обнулится, а уборка
            // за сорванной операцией его потом удалит. См. OutputCollision.
            if (OutputCollision.ChainIncludes(plan.Pieces.Select(p => p.Path).ToList(), outputPath))
            {
                return new PartitionMergeOutcome
                {
                    Status = PartitionMergeStatus.OutputInsideSource,
                    OutputPath = outputPath,
                    Plan = plan
                };
            }

            if (File.Exists(outputPath))
            {
                // Здесь строится окно — поток менять нельзя, см. пояснение выше.
                var decision = await conflicts.ResolveOutputFileConflictAsync(outputPath);

                if (decision == ConflictDecision.Cancel)
                {
                    return new PartitionMergeOutcome
                    {
                        Status = PartitionMergeStatus.CancelledBeforeStart,
                        OutputPath = outputPath,
                        Plan = plan
                    };
                }

                if (decision == ConflictDecision.UseAlternative)
                {
                    outputPath = FileConflictHelper.SuggestAlternativeFilePath(outputPath);
                    pathChanged = true;
                    log(Strings.Format("Common_FileConflictLog", outputPath));
                }
            }

            // Как и в остальных вкладках: работа с папкой назначения падает по причинам,
            // к самой сборке не относящимся, — и это не «сорвалось», а «нельзя начать».
            try
            {
                string outputFolder = Path.GetDirectoryName(outputPath) ?? ".";
                Directory.CreateDirectory(outputFolder);

                if (request.CheckDiskSpace)
                {
                    var check = spaceCheck is null
                        ? DiskSpaceHelper.CheckSpace(outputFolder, plan.TotalSize)
                        : spaceCheck(outputFolder, plan.TotalSize);

                    if (!check.HasEnoughSpace)
                    {
                        return new PartitionMergeOutcome
                        {
                            Status = PartitionMergeStatus.NotEnoughSpace,
                            OutputPath = outputPath,
                            OutputPathChanged = pathChanged,
                            Plan = plan,
                            SpaceCheck = check
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return new PartitionMergeOutcome
                {
                    Status = PartitionMergeStatus.OutputNotUsable,
                    OutputPath = outputPath,
                    OutputPathChanged = pathChanged,
                    Plan = plan,
                    ErrorMessage = ex.Message
                };
            }

            // Проверки пройдены — см. пояснение к тому же сигналу в ConvertOperation.
            onStarted?.Invoke();

            log(Strings.Format("PartitionMerge_StartLog", request.SourceFolder, outputPath));
            log(Strings.Format("PartitionMerge_PlanLog", plan.Pieces.Count, plan.TotalSize));

            var createdFiles = new List<string>();
            try
            {
                // Без передачи токена в Task.Run: отмена должна дойти до самой записи
                // и отработать штатно, а не превратиться в исключение до её начала.
                // Сама запись идёт в потоке пула (Task.Run), а вот итог и сообщения после
                // неё разбирает уже вызывающий — поэтому ConfigureAwait(false) и тут нет.
                var result = await Task.Run(() => PartitionMerger.MergeAsync(
                    plan, outputPath, request.FillByte, progress, log, ct, createdFiles, pauseController));

                log(Strings.Format("PartitionMerge_FinishedLog", result.PiecesUsed, result.TotalBytes));
                log(Strings.Format("Common_ResultHashLog", result.ResultHash));

                return new PartitionMergeOutcome
                {
                    Status = PartitionMergeStatus.Completed,
                    OutputPath = result.OutputPath,
                    OutputPathChanged = pathChanged,
                    Plan = plan,
                    PiecesUsed = result.PiecesUsed,
                    TotalBytes = result.TotalBytes,
                    ResultHash = result.ResultHash,
                    CreatedFileCount = createdFiles.Count
                };
            }
            catch (OperationCanceledException)
            {
                log(Strings.Get("PartitionMerge_CancelledLog"));
                IncompleteOutput.Remove(createdFiles, log);

                return new PartitionMergeOutcome
                {
                    Status = PartitionMergeStatus.Cancelled,
                    OutputPath = outputPath,
                    OutputPathChanged = pathChanged,
                    Plan = plan,
                    CreatedFileCount = createdFiles.Count
                };
            }
            catch (Exception ex)
            {
                log(Strings.Format("Common_ErrorLog", ex.Message));
                IncompleteOutput.Remove(createdFiles, log);

                return new PartitionMergeOutcome
                {
                    Status = PartitionMergeStatus.Failed,
                    OutputPath = outputPath,
                    OutputPathChanged = pathChanged,
                    Plan = plan,
                    ErrorMessage = ex.Message,
                    DiskFull = IncompleteOutput.IsDiskFull(ex),
                    CreatedFileCount = createdFiles.Count
                };
            }
        }
    }
}
