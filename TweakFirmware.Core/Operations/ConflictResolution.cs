using System.Threading.Tasks;

namespace TweakFirmware.Core.Operations
{
    /// <summary>Что делать, когда по пути назначения уже что-то лежит.</summary>
    public enum ConflictDecision
    {
        /// <summary>Писать поверх существующего.</summary>
        Overwrite,

        /// <summary>Взять соседнее свободное имя — папку или файл рядом.</summary>
        UseAlternative,

        /// <summary>Не начинать операцию вовсе.</summary>
        Cancel
    }

    /// <summary>
    /// Единственное место, где операции нужен ответ человека. Всё остальное они
    /// сообщают возвращаемым результатом, а не диалогом, — именно поэтому одну и ту же
    /// операцию можно запустить и из окна программы, и (в будущем) из очереди заданий,
    /// где показывать модальное окно на каждый файл недопустимо.
    /// </summary>
    public interface IConflictResolver
    {
        /// <summary>Конвертирование: в папке уже есть файлы с таким базовым именем.</summary>
        Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName);

        /// <summary>Сборка: файл результата уже существует.</summary>
        Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath);
    }

    /// <summary>
    /// Заранее выбранный ответ на все конфликты. Для очереди заданий это и есть штатный
    /// режим: политику выбирают один раз на всю очередь, а не отвечают на каждый файл.
    /// В тестах — способ прогнать конфликтные ветки без участия человека.
    /// </summary>
    public sealed class FixedConflictPolicy : IConflictResolver
    {
        private readonly ConflictDecision _decision;

        public FixedConflictPolicy(ConflictDecision decision) => _decision = decision;

        public Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName) =>
            Task.FromResult(_decision);

        public Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath) =>
            Task.FromResult(_decision);
    }
}
