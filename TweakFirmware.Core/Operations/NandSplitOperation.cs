using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Operations
{
    public enum NandSplitStatus
    {
        SourceNotFound,
        NotNandDump,
        NotEnoughSpace,
        Cancelled,
        Failed,
        Completed
    }

    public sealed class NandSplitOutcome
    {
        public NandSplitStatus Status { get; init; }
        public string MainPath { get; init; } = "";
        public string SparePath { get; init; } = "";
        public long MainBytes { get; init; }
        public long SpareBytes { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    /// <summary>
    /// Разрезание дампа NAND на две части: данные и служебные области.
    ///
    /// Нужно, когда дамп надо отдать программе, которая про служебные области ничего не
    /// знает, или наоборот — разобраться, что в них записано. У раскладки Dune HD
    /// служебные байты разбросаны внутри страницы и собрать их в осмысленный отдельный
    /// файл нельзя, поэтому там пишется только main.
    /// </summary>
    public static class NandSplitOperation
    {
        private const string MainFileName = "main.bin";
        private const string SpareFileName = "spare.bin";

        public static async Task<NandSplitOutcome> RunAsync(
            string sourcePath,
            NandGeometry? geometry,
            string outputFolder,
            IAnalysisHost host,
            CancellationToken ct = default,
            PauseController? pause = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return new NandSplitOutcome { Status = NandSplitStatus.SourceNotFound };

            if (geometry is null)
                return new NandSplitOutcome { Status = NandSplitStatus.NotNandDump };

            string folder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "."
                : outputFolder;

            string mainPath = Path.Combine(folder, MainFileName);
            string sparePath = Path.Combine(folder, SpareFileName);

            var created = new List<string>();

            try
            {
                return await SplitAsync(sourcePath, geometry, folder, mainPath, sparePath, created, host, ct, pause)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                IncompleteOutput.Remove(created, message => host.Log(message, AnalysisLogLevel.Warning));
                return new NandSplitOutcome { Status = NandSplitStatus.Cancelled };
            }
            catch (Exception ex)
            {
                IncompleteOutput.Remove(created, message => host.Log(message, AnalysisLogLevel.Warning));
                return new NandSplitOutcome
                {
                    Status = IncompleteOutput.IsDiskFull(ex) ? NandSplitStatus.NotEnoughSpace : NandSplitStatus.Failed,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static async Task<NandSplitOutcome> SplitAsync(
            string sourcePath,
            NandGeometry geometry,
            string folder,
            string mainPath,
            string sparePath,
            List<string> created,
            IAnalysisHost host,
            CancellationToken ct,
            PauseController? pause)
        {
            Directory.CreateDirectory(folder);

            using var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new NandDumpReader(file, geometry, ownsStream: false);

            // У Dune HD служебные байты разбросаны внутри страницы кусками по тринадцать
            // байт вперемешку с мусором; отдельный файл из них смысла не имеет.
            bool writeSpare = geometry.Layout != NandPageLayout.DuneHd;

            long pages = reader.PageCount;

            var space = DiskSpaceHelper.CheckSpace(folder, reader.PhysicalSize);
            if (!space.HasEnoughSpace)
            {
                return new NandSplitOutcome
                {
                    Status = NandSplitStatus.NotEnoughSpace,
                    ErrorMessage = Strings.Format("Extract_NotEnoughSpace",
                        DiskSpaceHelper.FormatBytes(reader.PhysicalSize), DiskSpaceHelper.FormatBytes(space.AvailableBytes))
                };
            }

            created.Add(mainPath);
            if (writeSpare) created.Add(sparePath);

            var page = new byte[geometry.Page];
            long mainBytes = 0, spareBytes = 0;

            // Обе части пишутся за один проход. Оригинал сначала накапливал весь spare
            // в памяти, а потом копировал его ещё раз перед записью — на дампе в восемь
            // гигабайт это сотни мегабайт впустую.
            using (var mainOut = new FileStream(mainPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var spareOut = writeSpare
                       ? new FileStream(sparePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)
                       : null)
            {
                for (long i = 0; i < pages; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

                    if ((i & 0xFFF) == 0)
                        host.Progress?.Report(new AnalysisProgress(Strings.Get("Extract_StageSplitNand"), i, pages));

                    reader.ReadRawPage(i, page);

                    await mainOut.WriteAsync(page.AsMemory(0, geometry.Main), ct).ConfigureAwait(false);
                    mainBytes += geometry.Main;

                    if (spareOut is not null)
                    {
                        await spareOut.WriteAsync(page.AsMemory(geometry.Main, geometry.Spare), ct).ConfigureAwait(false);
                        spareBytes += geometry.Spare;
                    }
                }
            }

            host.Log(Strings.Format("Extract_NandSplitDone", mainBytes, spareBytes), AnalysisLogLevel.Found);

            return new NandSplitOutcome
            {
                Status = NandSplitStatus.Completed,
                MainPath = mainPath,
                SparePath = writeSpare ? sparePath : "",
                MainBytes = mainBytes,
                SpareBytes = spareBytes
            };
        }
    }
}
