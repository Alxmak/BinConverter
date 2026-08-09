using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.Analysis
{
    /// <summary>Как показать строку журнала разбора.</summary>
    public enum AnalysisLogLevel
    {
        /// <summary>Обычное сообщение о ходе работы.</summary>
        Info,

        /// <summary>Находка: опознана разметка, найдена файловая система.</summary>
        Found,

        /// <summary>Что-то подозрительное, но разбор продолжается.</summary>
        Warning,

        /// <summary>Дальше по этой ветке идти нельзя.</summary>
        Error
    }

    /// <summary>
    /// Всё, что разбору дампа нужно от внешнего мира: писать в журнал, показывать прогресс
    /// и — в двух местах — спросить человека.
    ///
    /// Существует потому, что в <c>TweakFirmware.Core</c> принято не открывать диалогов и
    /// не печатать текстов: операция возвращает факты, а показывает их вызывающая сторона.
    /// В оригинальном скрипте было наоборот — детектор сам звал messageBox посреди работы,
    /// из-за чего разбор нельзя было ни протестировать, ни запустить пакетно.
    ///
    /// Спрашивать приходится ровно дважды: когда не удалось определить размер страницы
    /// NAND и когда надо решить, искать ли внутри разделов файловые системы.
    /// </summary>
    public interface IAnalysisHost
    {
        void Log(string message, AnalysisLogLevel level = AnalysisLogLevel.Info);

        /// <summary>
        /// Автоопределение main/spare не справилось. Возвращает выбранный индекс варианта
        /// или <c>null</c>, если пользователь отказался — тогда разбор идёт без NAND-ветки.
        /// </summary>
        Task<int?> AskNandGeometryAsync(IReadOnlyList<NandGeometryOption> options, CancellationToken ct);

        /// <summary>Вопрос «да/нет». Отказ — это <c>false</c>, а не исключение.</summary>
        Task<bool> ConfirmAsync(string question, CancellationToken ct);

        /// <summary>Куда сообщать о ходе долгих проходов по дампу. Может быть <c>null</c>.</summary>
        IProgress<AnalysisProgress>? Progress { get; }
    }

    /// <summary>Ход длинной операции: что делаем и сколько уже прошли.</summary>
    public readonly record struct AnalysisProgress(string Stage, long Done, long Total);

    /// <summary>
    /// Хост, который ничего не показывает и на все вопросы отвечает заранее заданным.
    /// Нужен тестам и любому запуску без интерфейса; заодно служит образцом того,
    /// что реализация обязана уметь.
    /// </summary>
    public sealed class SilentAnalysisHost : IAnalysisHost
    {
        private readonly bool _confirmAnswer;
        private readonly int? _geometryChoice;

        public SilentAnalysisHost(bool confirmAnswer = false, int? geometryChoice = null)
        {
            _confirmAnswer = confirmAnswer;
            _geometryChoice = geometryChoice;
        }

        /// <summary>Всё, что было записано в журнал, — чтобы тест мог это проверить.</summary>
        public List<(string Message, AnalysisLogLevel Level)> Messages { get; } = new();

        public void Log(string message, AnalysisLogLevel level = AnalysisLogLevel.Info) =>
            Messages.Add((message, level));

        public Task<int?> AskNandGeometryAsync(IReadOnlyList<NandGeometryOption> options, CancellationToken ct) =>
            Task.FromResult(_geometryChoice);

        public Task<bool> ConfirmAsync(string question, CancellationToken ct) =>
            Task.FromResult(_confirmAnswer);

        public IProgress<AnalysisProgress>? Progress => null;
    }
}
