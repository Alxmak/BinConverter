using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Хост разбора, который запоминает всё, о чём его известили.
    ///
    /// <see cref="SilentAnalysisHost"/> из Core для этого не годится: у него
    /// <c>Progress</c> равен <c>null</c> — он и задуман как «наружу ничего не показываем».
    /// А проверять надо как раз то, что уходит в <c>Progress</c>: как часто приходят
    /// сообщения о ходе работы и доходит ли полоса до конца. Ни то, ни другое из самого
    /// результата операции не видно.
    /// </summary>
    internal sealed class RecordingAnalysisHost : IAnalysisHost
    {
        private sealed class Recorder : IProgress<AnalysisProgress>
        {
            private readonly List<AnalysisProgress> _target;

            public Recorder(List<AnalysisProgress> target) => _target = target;

            public void Report(AnalysisProgress value) => _target.Add(value);
        }

        private readonly List<AnalysisProgress> _reports = new();

        public RecordingAnalysisHost() => Progress = new Recorder(_reports);

        /// <summary>Все сообщения о ходе работы, по порядку.</summary>
        public IReadOnlyList<AnalysisProgress> Reports => _reports;

        public List<string> Messages { get; } = new();

        public void Log(string message, AnalysisLogLevel level = AnalysisLogLevel.Info) => Messages.Add(message);

        /// <summary>Отказ от выбора: разбор в тестах не должен ничего спрашивать.</summary>
        public Task<int?> AskNandGeometryAsync(IReadOnlyList<NandGeometryOption> options, CancellationToken ct) =>
            Task.FromResult<int?>(null);

        public Task<bool> ConfirmAsync(string question, CancellationToken ct) => Task.FromResult(false);

        public IProgress<AnalysisProgress>? Progress { get; }
    }
}
