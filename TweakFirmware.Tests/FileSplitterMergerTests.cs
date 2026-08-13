using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Тесты ядра конвертера: разбиение, сборка, хэши.
    /// Каждый тест создаёт свою временную папку с "фейковыми" bin-файлами
    /// (случайные байты — реальный дамп eMMC не нужен, важна только точность
    /// байтовой арифметики) и удаляет её после себя.
    /// </summary>
    public class FileSplitterMergerTests : IDisposable
    {
        private readonly string _tempDir;

        public FileSplitterMergerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "TweakFirmwareTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* временные файлы — не критично, если не удалились с первого раза */ }
        }

        private string CreateRandomFile(string name, long sizeBytes, int seed = 42)
        {
            string path = Path.Combine(_tempDir, name);
            var rnd = new Random(seed);
            byte[] buffer = new byte[64 * 1024];
            long remaining = sizeBytes;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, remaining);
                rnd.NextBytes(buffer.AsSpan(0, chunk));
                fs.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
            return path;
        }

        // ===================== ОСНОВНОЙ СЦЕНАРИЙ =====================

        [Fact]
        public async Task Split_ThenMerge_ProducesByteIdenticalFile()
        {
            // Файл 5 МБ, лимит части 1 МБ -> ожидаем ровно 5 файлов (base + part1..part4)
            string source = CreateRandomFile("source.bin", 5 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "split_out");
            long maxPartSize = 1 * 1024 * 1024;

            var splitResult = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: true, progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.True(splitResult.HashesMatch, "SHA-256 источника и виртуальной склейки частей должны совпадать сразу после разбиения");
            Assert.Equal(5, splitResult.PartsCreated);

            string basePath = Path.Combine(outFolder, "emmc.bin");
            string mergedPath = Path.Combine(_tempDir, "merged.bin");

            var mergeResult = await FileMerger.MergeAsync(basePath, mergedPath, progress: null, log: _ => { }, ct: CancellationToken.None);

            string sourceHash = await HashHelper.ComputeFileHashAsync(source);
            Assert.Equal(sourceHash, mergeResult.MergedHash);

            // Дополнительная проверка "в лоб" — байт в байт через File.ReadAllBytes
            byte[] originalBytes = await File.ReadAllBytesAsync(source);
            byte[] mergedBytes = await File.ReadAllBytesAsync(mergedPath);
            Assert.Equal(originalBytes, mergedBytes);
        }

        // ===================== ГРАНИЧНЫЕ СЛУЧАИ =====================

        [Fact]
        public async Task Split_FileSizeExactMultipleOfLimit_NoEmptyTrailingPart()
        {
            // Файл ровно 3 x лимит -> ожидаем ровно 3 файла, БЕЗ лишнего пустого part3
            long maxPartSize = 1 * 1024 * 1024;
            string source = CreateRandomFile("exact.bin", maxPartSize * 3);
            string outFolder = Path.Combine(_tempDir, "exact_out");

            var result = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: true, progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.Equal(3, result.PartsCreated);
            Assert.True(result.HashesMatch);
            Assert.False(File.Exists(Path.Combine(outFolder, "emmc.bin.part3")),
                "Не должно создаваться лишнего пустого файла ровно на границе размера");
        }

        [Fact]
        public async Task Split_FileSmallerThanLimit_ProducesSingleFileNoParts()
        {
            long maxPartSize = 10 * 1024 * 1024;
            string source = CreateRandomFile("small.bin", 2 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "small_out");

            var result = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: true, progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.Equal(1, result.PartsCreated);
            Assert.False(File.Exists(Path.Combine(outFolder, "emmc.bin.part1")));
        }

        [Fact]
        public async Task Split_EmptyFile_DoesNotThrow()
        {
            string source = CreateRandomFile("empty.bin", 0);
            string outFolder = Path.Combine(_tempDir, "empty_out");

            var result = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", 1024 * 1024,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.Equal(1, result.PartsCreated);
            string createdPath = Path.Combine(outFolder, "emmc.bin");
            Assert.True(File.Exists(createdPath));
            Assert.Equal(0, new FileInfo(createdPath).Length);
        }

        [Fact]
        public async Task Split_OneByteOverLimit_CreatesSecondFileWithOneByte()
        {
            // Проверяем твоё исходное требование дословно:
            // 4 055 041-й КБ уже должен уйти в новый файл.
            // Здесь то же самое поведение на маленьких числах: лимит + 1 байт.
            long maxPartSize = 1024; // 1 КБ для скорости теста
            string source = CreateRandomFile("overby1.bin", maxPartSize + 1);
            string outFolder = Path.Combine(_tempDir, "overby1_out");

            var result = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: true, progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.Equal(2, result.PartsCreated);
            Assert.Equal(maxPartSize, new FileInfo(Path.Combine(outFolder, "emmc.bin")).Length);
            Assert.Equal(1, new FileInfo(Path.Combine(outFolder, "emmc.bin.part1")).Length);
        }

        [Fact]
        public async Task HashHelper_ComputePartsHashAsync_ReportsProgressUpToTotal()
        {
            long maxPartSize = 500 * 1024;
            string source = CreateRandomFile("hashprogress.bin", maxPartSize * 3);
            string outFolder = Path.Combine(_tempDir, "hashprogress_out");

            var splitResult = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            long lastDone = 0, lastTotal = 0;
            var progress = new SyncProgress<(long done, long total)>(p => { lastDone = p.done; lastTotal = p.total; });

            string basePath = Path.Combine(outFolder, "emmc.bin");
            await HashHelper.ComputePartsHashAsync(basePath, splitResult.PartsCreated - 1, CancellationToken.None, progress);

            Assert.Equal(lastTotal, lastDone); // на последнем отчёте done должен догнать total
            Assert.Equal(maxPartSize * 3, lastTotal);
        }

        // ===================== HashHelper: поиск цепочки частей =====================

        [Fact]
        public void HashHelper_FindPartChain_RefusesChainWithGap()
        {
            string outFolder = Path.Combine(_tempDir, "chain_out");
            Directory.CreateDirectory(outFolder);
            string basePath = Path.Combine(outFolder, "emmc.bin");

            File.WriteAllBytes(basePath, new byte[10]);
            File.WriteAllBytes(basePath + ".part1", new byte[10]);
            File.WriteAllBytes(basePath + ".part2", new byte[10]);
            // .part3 намеренно не создаём, .part4 создаём — цепочка разорвана.
            File.WriteAllBytes(basePath + ".part4", new byte[10]);

            // Раньше поиск молча останавливался на пропуске и возвращал три файла:
            // сборка проходила «успешно» и давала укороченный файл, неотличимый на вид
            // от целой прошивки. Теперь это отказ.
            var error = Assert.Throws<FileNotFoundException>(() => HashHelper.FindPartChain(basePath));

            Assert.Contains("3", error.Message);
            Assert.Contains("4", error.Message);
        }

        [Fact]
        public void HashHelper_ResolveBasePath_HandlesPartFileNameCorrectly()
        {
            string fromPart = HashHelper.ResolveBasePath(@"D:\Dumps\emmc.bin.part7");
            Assert.Equal(@"D:\Dumps\emmc.bin", fromPart);

            string fromBase = HashHelper.ResolveBasePath(@"D:\Dumps\emmc.bin");
            Assert.Equal(@"D:\Dumps\emmc.bin", fromBase);
        }

        // ===================== FileConflictHelper =====================

        [Fact]
        public void FileConflictHelper_AnyConflict_DetectsExistingBaseFile()
        {
            string outFolder = Path.Combine(_tempDir, "conflict_out");
            Directory.CreateDirectory(outFolder);
            File.WriteAllBytes(Path.Combine(outFolder, "emmc.bin"), new byte[1]);

            Assert.True(FileConflictHelper.AnyConflict(outFolder, "emmc.bin"));
        }

        [Fact]
        public void FileConflictHelper_AnyConflict_DetectsExistingPartFile()
        {
            string outFolder = Path.Combine(_tempDir, "conflict_out2");
            Directory.CreateDirectory(outFolder);
            File.WriteAllBytes(Path.Combine(outFolder, "emmc.bin.part1"), new byte[1]);

            Assert.True(FileConflictHelper.AnyConflict(outFolder, "emmc.bin"));
        }

        [Fact]
        public void FileConflictHelper_AnyConflict_FalseWhenFolderEmptyOrMissing()
        {
            string outFolder = Path.Combine(_tempDir, "no_conflict_out");
            // папка ещё не создана
            Assert.False(FileConflictHelper.AnyConflict(outFolder, "emmc.bin"));
        }

        [Fact]
        public void FileConflictHelper_SuggestAlternativeFolder_SkipsExistingNonEmptyFolders()
        {
            string baseFolder = Path.Combine(_tempDir, "output");
            Directory.CreateDirectory(baseFolder);
            File.WriteAllBytes(Path.Combine(baseFolder, "x.bin"), new byte[1]);

            string folder2 = baseFolder + "_2";
            Directory.CreateDirectory(folder2);
            File.WriteAllBytes(Path.Combine(folder2, "y.bin"), new byte[1]); // тоже занята

            string suggested = FileConflictHelper.SuggestAlternativeFolder(baseFolder);

            Assert.Equal(baseFolder + "_3", suggested);
        }

        [Fact]
        public void FileConflictHelper_SuggestAlternativeFilePath_SkipsExistingFiles()
        {
            string file1 = Path.Combine(_tempDir, "result.bin");
            File.WriteAllBytes(file1, new byte[1]);
            string file2 = Path.Combine(_tempDir, "result_2.bin");
            File.WriteAllBytes(file2, new byte[1]);

            string suggested = FileConflictHelper.SuggestAlternativeFilePath(file1);

            Assert.Equal(Path.Combine(_tempDir, "result_3.bin"), suggested);
        }

        // ===================== DiskSpaceHelper =====================

        [Fact]
        public void DiskSpaceHelper_CheckSpace_ReturnsPositiveAvailableBytes()
        {
            // Просто проверяем, что метод не падает и возвращает разумное значение
            // для существующего временного диска — точное число свободного места
            // непредсказуемо и не тестируется.
            var result = DiskSpaceHelper.CheckSpace(_tempDir, dataSizeBytes: 1024);

            Assert.True(result.AvailableBytes > 0);
            Assert.Equal((long)(1024 * 1.05), result.RequiredBytes);
        }

        // ===================== Отслеживание созданных файлов (для очистки при отмене/ошибке) =====================

        [Fact]
        public async Task Split_TracksCreatedFilePaths()
        {
            string source = CreateRandomFile("track.bin", 3 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "track_out");
            long maxPartSize = 1 * 1024 * 1024;
            var createdFiles = new List<string>();

            var result = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None,
                createdFilePaths: createdFiles);

            Assert.Equal(result.PartsCreated, createdFiles.Count);
            foreach (var path in createdFiles) Assert.True(File.Exists(path));
        }

        [Fact]
        public async Task Merge_TracksCreatedFilePath()
        {
            string source = CreateRandomFile("track2.bin", 3 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "track2_out");
            long maxPartSize = 1 * 1024 * 1024;
            await FileSplitter.SplitAsync(source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            string mergedPath = Path.Combine(_tempDir, "track2_merged.bin");
            var createdFiles = new List<string>();

            await FileMerger.MergeAsync(Path.Combine(outFolder, "emmc.bin"), mergedPath,
                progress: null, log: _ => { }, ct: CancellationToken.None, createdFilePaths: createdFiles);

            Assert.Single(createdFiles);
            Assert.Equal(mergedPath, createdFiles[0]);
        }

        [Fact]
        public async Task Split_CancelledMidway_CreatedFilesListReflectsPhysicalState()
        {
            // Список созданных файлов должен точно отражать то, что реально успело
            // появиться на диске к моменту отмены — на этом строится очистка в UI.
            string source = CreateRandomFile("cancel.bin", 5 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "cancel_out");
            long maxPartSize = 512 * 1024;
            var createdFiles = new List<string>();
            var cts = new CancellationTokenSource();

            // Именно SyncProgress, а не System.Progress: последний доставляет колбэк через
            // пул потоков, из-за чего Cancel() может сработать уже после конца операции.
            var progress = new SyncProgress<SplitProgress>(p =>
            {
                if (p.CurrentFileIndex >= 3) cts.Cancel();
            });

            // TaskCanceledException — это подкласс OperationCanceledException (так и должно
            // выбрасываться при отмене чтения/записи файла), поэтому проверяем через
            // ThrowsAnyAsync, а не ThrowsAsync (которая требует точного совпадения типа).
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                FileSplitter.SplitAsync(source, outFolder, "emmc.bin", maxPartSize,
                    verifyHash: false, progress: progress, log: _ => { }, ct: cts.Token, createdFilePaths: createdFiles));

            Assert.True(createdFiles.Count >= 2, "К моменту отмены должно быть создано хотя бы 2 файла");
            foreach (var path in createdFiles)
                Assert.True(File.Exists(path), "Core не удаляет файлы сам — это ответственность UI-слоя");
        }

        // ===================== SizeFormatHelper =====================

        [Theory]
        [InlineData(500L * 1024 * 1024, "МБ")]      // 500 МБ -> МБ
        [InlineData(1024L * 1024 * 1024, "ГБ")]      // ровно 1 ГБ -> ГБ
        [InlineData(7L * 1024 * 1024 * 1024, "ГБ")]  // 7 ГБ -> ГБ
        public void SizeFormatHelper_Format_UsesCorrectUnit(long bytes, string expectedUnit)
        {
            string result = SizeFormatHelper.Format(bytes);
            Assert.Contains(expectedUnit, result);
            Assert.Contains(bytes.ToString("N0"), result); // байты в скобках тоже должны быть
        }

        // ===================== PauseController =====================

        [Fact]
        public async Task PauseController_WaitIfPausedAsync_BlocksUntilResume()
        {
            var pc = new PauseController();
            pc.Pause();

            var waitTask = pc.WaitIfPausedAsync(CancellationToken.None);
            await Task.Delay(50); // даём шанс завершиться, если пауза не сработала
            Assert.False(waitTask.IsCompleted, "Ожидание должно блокироваться, пока не вызван Resume()");

            pc.Resume();
            await waitTask; // не должно зависнуть
            Assert.True(waitTask.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task PauseController_WaitIfPausedAsync_NotPaused_ReturnsImmediately()
        {
            var pc = new PauseController();
            var task = pc.WaitIfPausedAsync(CancellationToken.None);
            await Task.Delay(20);
            Assert.True(task.IsCompleted);
        }

        [Fact]
        public async Task PauseController_WaitIfPausedAsync_CancelledWhilePaused_Throws()
        {
            var pc = new PauseController();
            pc.Pause();
            var cts = new CancellationTokenSource();

            var waitTask = pc.WaitIfPausedAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
        }

        [Fact]
        public async Task Split_WithPauseController_ActuallyPauses()
        {
            // Файл ЗАВЕДОМО больше буфера чтения (4 МБ) — иначе весь файл прочитается
            // за одну итерацию, и progress.Report() (вызывается раз за итерацию, а не
            // раз за создаваемую часть) сработает всего один раз, и условие "поставить
            // паузу на N-м отчёте" никогда не выполнится.
            string source = CreateRandomFile("pause.bin", 9 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "pause_out");
            long maxPartSize = 1 * 1024 * 1024;
            var pc = new PauseController();

            bool paused = false;
            var progress = new SyncProgress<SplitProgress>(p =>
            {
                if (!paused)
                {
                    paused = true;
                    pc.Pause();
                    _ = Task.Delay(50).ContinueWith(_ => pc.Resume());
                }
            });

            var splitTask = FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: progress, log: _ => { }, ct: CancellationToken.None,
                createdFilePaths: null, hashVerifyProgress: null, pauseController: pc);

            // Task.WhenAny с таймаутом — чтобы при реальном зависании тест упал
            // с понятным сообщением, а не подвис навсегда.
            var completed = await Task.WhenAny(splitTask, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(paused, "Пауза должна была сработать хотя бы раз за время теста");
            Assert.Same(splitTask, completed);

            var result = await splitTask;
            Assert.True(result.PartsCreated > 0);
        }

        // ===================== FileSplitter.CalculateExpectedPartCount =====================

        [Theory]
        [InlineData(0, 1000, 1)]           // пустой файл — всё равно 1 (пустой) файл
        [InlineData(500, 1000, 1)]         // меньше лимита — 1 файл
        [InlineData(1000, 1000, 1)]        // ровно лимит — 1 файл
        [InlineData(1001, 1000, 2)]        // на 1 байт больше — уже 2 файла
        [InlineData(3000, 1000, 3)]        // ровно кратно — без лишнего файла
        [InlineData(3001, 1000, 4)]        // чуть больше кратного — на 1 файл больше
        public void FileSplitter_CalculateExpectedPartCount_MatchesFormula(long sourceSize, long maxPartSize, int expected)
        {
            int actual = FileSplitter.CalculateExpectedPartCount(sourceSize, maxPartSize);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task FileSplitter_CalculateExpectedPartCount_MatchesActualSplitResult()
        {
            // Предрасчёт и реальный результат разбиения должны совпадать —
            // это то самое число, которое видит пользователь ДО нажатия "Начать".
            long maxPartSize = 700 * 1024; // 700 КБ
            string source = CreateRandomFile("preview.bin", (long)(maxPartSize * 4.3));
            string outFolder = Path.Combine(_tempDir, "preview_out");

            int predicted = FileSplitter.CalculateExpectedPartCount(new FileInfo(source).Length, maxPartSize);

            var result = await FileSplitter.SplitAsync(
                source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.Equal(predicted, result.PartsCreated);
        }

        [Fact]
        public async Task HashHelper_ComputeFileHashAsync_ReportsProgressUpToTotal()
        {
            string path = CreateRandomFile("filehashprogress.bin", 700 * 1024);
            long lastDone = 0, lastTotal = 0;
            var progress = new SyncProgress<(long done, long total)>(p => { lastDone = p.done; lastTotal = p.total; });

            await HashHelper.ComputeFileHashAsync(path, CancellationToken.None, progress);

            Assert.Equal(lastTotal, lastDone);
            Assert.Equal(700 * 1024, lastTotal);
        }

        [Fact]
        public async Task HashHelper_ComputeFileHashAsync_IsDeterministic()
        {
            // Один и тот же контент должен всегда давать один и тот же хэш
            string path = CreateRandomFile("determinism.bin", 500_000, seed: 7);

            string hash1 = await HashHelper.ComputeFileHashAsync(path);
            string hash2 = await HashHelper.ComputeFileHashAsync(path);

            Assert.Equal(hash1, hash2);
            Assert.Equal(64, hash1.Length); // SHA-256 в hex = 64 символа
        }

        // ===================== Некорректные входные данные =====================

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Split_InvalidMaxPartSize_ThrowsArgumentOutOfRangeException(long invalidLimit)
        {
            string source = CreateRandomFile("invalid_limit.bin", 1024);
            string outFolder = Path.Combine(_tempDir, "invalid_limit_out");

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                FileSplitter.SplitAsync(source, outFolder, "emmc.bin", invalidLimit,
                    verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None));
        }

        [Fact]
        public void CalculateExpectedPartCount_InvalidMaxPartSize_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FileSplitter.CalculateExpectedPartCount(1000, 0));
        }

        [Fact]
        public async Task Split_SourceFileMissing_ThrowsFileNotFoundException()
        {
            string missingSource = Path.Combine(_tempDir, "does_not_exist.bin");
            string outFolder = Path.Combine(_tempDir, "missing_source_out");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                FileSplitter.SplitAsync(missingSource, outFolder, "emmc.bin", 1024,
                    verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None));
        }

        [Fact]
        public void HashHelper_FindPartChain_ThrowsWhenBaseFileMissing()
        {
            string basePath = Path.Combine(_tempDir, "no_such_base.bin");
            Assert.Throws<FileNotFoundException>(() => HashHelper.FindPartChain(basePath));
        }

        // ===================== Сборка: повреждённые/пропущенные/лишние файлы =====================

        [Fact]
        public async Task Merge_CorruptedPart_ProducesDifferentHashThanOriginal()
        {
            // Ядро не обязано само детектировать повреждение — это делает сверка хэшей
            // на уровне UI. Здесь фиксируем именно это: результат молча собирается,
            // но его хэш отличается от хэша исходного файла.
            string source = CreateRandomFile("corrupt_source.bin", 3 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "corrupt_out");
            long maxPartSize = 1 * 1024 * 1024;

            await FileSplitter.SplitAsync(source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            string corruptedPart = Path.Combine(outFolder, "emmc.bin.part1");
            byte[] bytes = await File.ReadAllBytesAsync(corruptedPart);
            bytes[0] = (byte)~bytes[0]; // портим один байт
            await File.WriteAllBytesAsync(corruptedPart, bytes);

            string mergedPath = Path.Combine(_tempDir, "corrupt_merged.bin");
            var mergeResult = await FileMerger.MergeAsync(Path.Combine(outFolder, "emmc.bin"), mergedPath,
                progress: null, log: _ => { }, ct: CancellationToken.None);

            string sourceHash = await HashHelper.ComputeFileHashAsync(source);
            Assert.NotEqual(sourceHash, mergeResult.MergedHash);
        }

        [Fact]
        public async Task Merge_MissingMiddlePart_IsRefusedInsteadOfProducingTruncatedFile()
        {
            string source = CreateRandomFile("gap_source.bin", 4 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "gap_out");
            long maxPartSize = 1 * 1024 * 1024; // ожидаем base + part1..part3

            await FileSplitter.SplitAsync(source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            File.Delete(Path.Combine(outFolder, "emmc.bin.part2")); // part3 остаётся, но недостижим

            string mergedPath = Path.Combine(_tempDir, "gap_merged.bin");

            // Прежнее поведение — собрать base + part1 и отчитаться об успехе — давало
            // файл вдвое короче прошивки, который выглядит настоящим. Теперь сборка
            // отказывается: пропуск в середине цепочки чинится только возвратом части.
            await Assert.ThrowsAsync<FileNotFoundException>(() => FileMerger.MergeAsync(
                Path.Combine(outFolder, "emmc.bin"), mergedPath,
                progress: null, log: _ => { }, ct: CancellationToken.None));

            Assert.False(File.Exists(mergedPath), "Файл результата не должен появиться");
        }

        [Fact]
        public async Task Merge_ExtraUnrelatedFilesInFolder_AreIgnored()
        {
            string source = CreateRandomFile("extra_source.bin", 2 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "extra_out");
            long maxPartSize = 1 * 1024 * 1024;

            await FileSplitter.SplitAsync(source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            // Файлы, не подходящие под шаблон "emmc.bin" / "emmc.bin.partN", не должны влиять на сборку
            File.WriteAllText(Path.Combine(outFolder, "notes.txt"), "не часть цепочки");
            File.WriteAllBytes(Path.Combine(outFolder, "emmc.bin.backup"), new byte[] { 1, 2, 3 });

            string mergedPath = Path.Combine(_tempDir, "extra_merged.bin");
            var mergeResult = await FileMerger.MergeAsync(Path.Combine(outFolder, "emmc.bin"), mergedPath,
                progress: null, log: _ => { }, ct: CancellationToken.None);

            string sourceHash = await HashHelper.ComputeFileHashAsync(source);
            Assert.Equal(sourceHash, mergeResult.MergedHash);
        }

        [Fact]
        public async Task Merge_SingleFileNoParts_ReturnsSameBytes()
        {
            string source = CreateRandomFile("single_source.bin", 128 * 1024);
            string basePath = Path.Combine(_tempDir, "single_base.bin");
            File.Copy(source, basePath);

            string mergedPath = Path.Combine(_tempDir, "single_merged.bin");
            var mergeResult = await FileMerger.MergeAsync(basePath, mergedPath,
                progress: null, log: _ => { }, ct: CancellationToken.None);

            Assert.Equal(1, mergeResult.PartsUsed);
            string sourceHash = await HashHelper.ComputeFileHashAsync(source);
            Assert.Equal(sourceHash, mergeResult.MergedHash);
        }

        [Fact]
        public async Task Merge_CancelledMidway_CreatedFileTrackedAndIncomplete()
        {
            string source = CreateRandomFile("merge_cancel_source.bin", 6 * 1024 * 1024);
            string outFolder = Path.Combine(_tempDir, "merge_cancel_out");
            long maxPartSize = 1 * 1024 * 1024;

            await FileSplitter.SplitAsync(source, outFolder, "emmc.bin", maxPartSize,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            string mergedPath = Path.Combine(_tempDir, "merge_cancel_merged.bin");
            var createdFiles = new List<string>();
            var cts = new CancellationTokenSource();

            // System.Progress здесь делал тест нестабильным: колбэк уходил в пул потоков,
            // и на быстрой машине отмена доходила до цикла уже после записи всех частей —
            // исключение вылетало из ReadAsync, но файл успевал стать полным. SyncProgress
            // вызывает Cancel() строго внутри цикла, поэтому сборка рвётся на середине.
            var progress = new SyncProgress<MergeProgress>(p =>
            {
                if (p.CurrentFileIndex >= 3) cts.Cancel();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                FileMerger.MergeAsync(Path.Combine(outFolder, "emmc.bin"), mergedPath,
                    progress, log: _ => { }, ct: cts.Token, createdFilePaths: createdFiles));

            Assert.Single(createdFiles);
            Assert.True(File.Exists(mergedPath));
            Assert.True(new FileInfo(mergedPath).Length < new FileInfo(source).Length,
                "Файл результата на момент отмены должен быть короче исходного — сборка прервана до конца");
        }

        // ===================== DiskSpaceHelper: нехватка места =====================

        [Fact]
        public void DiskSpaceHelper_CheckSpace_DetectsInsufficientSpace()
        {
            // Запрашиваем заведомо больше места, чем есть на любом реальном диске
            var result = DiskSpaceHelper.CheckSpace(_tempDir, dataSizeBytes: long.MaxValue / 4);

            Assert.False(result.HasEnoughSpace);
            Assert.True(result.MissingBytes > 0);
        }

        // ===================== SizeFormatHelper: дополнительные границы =====================

        [Theory]
        [InlineData(0L, "МБ")]
        [InlineData(1024L * 1024 * 1024 - 1, "МБ")] // на 1 байт меньше 1 ГБ — ещё МБ
        public void SizeFormatHelper_Format_BoundaryValues_UseCorrectUnit(long bytes, string expectedUnit)
        {
            string result = SizeFormatHelper.Format(bytes);
            Assert.Contains(expectedUnit, result);
        }
    }
}
