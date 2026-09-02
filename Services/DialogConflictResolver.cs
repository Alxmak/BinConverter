using System.IO;
using System.Threading.Tasks;
using TweakFirmware.Core.Localization;
using TweakFirmware.Core.Operations;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Спрашивает пользователя про конфликт имён обычным диалогом программы.
    /// Это вся «оконная» часть операций: остальное они сообщают возвращаемым итогом.
    /// Очередь заданий, когда появится, подставит вместо этого класса
    /// <see cref="FixedConflictPolicy"/> — политику, выбранную один раз на всю очередь.
    /// </summary>
    public sealed class DialogConflictResolver : IConflictResolver
    {
        public async Task<ConflictDecision> ResolveOutputFolderConflictAsync(string outputFolder, string baseFileName)
        {
            var choice = await DialogService.ShowConfirmAsync(
                Strings.Get("Convert_ConflictTitle"),
                Strings.Format("Convert_ConflictMessage", baseFileName),
                Strings.Get("Common_OverwriteChoice"),
                Strings.Get("Convert_NewFolderNearby"),
                Strings.Get("Common_CancelButton"));

            return Map(choice);
        }

        public async Task<ConflictDecision> ResolveOutputFileConflictAsync(string outputPath)
        {
            var choice = await DialogService.ShowConfirmAsync(
                Strings.Get("Merge_FileExistsTitle"),
                Strings.Format("Merge_FileExistsMessage", Path.GetFileName(outputPath)),
                Strings.Get("Common_OverwriteChoice"),
                Strings.Get("Merge_NewNameNearby"),
                Strings.Get("Common_CancelButton"));

            return Map(choice);
        }

        private static ConflictDecision Map(DialogChoice choice) => choice switch
        {
            DialogChoice.Primary => ConflictDecision.Overwrite,
            DialogChoice.Secondary => ConflictDecision.UseAlternative,
            _ => ConflictDecision.Cancel
        };
    }
}
