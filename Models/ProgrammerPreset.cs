using CommunityToolkit.Mvvm.ComponentModel;

namespace TweakFirmware.Models
{
    /// <summary>
    /// Пресет программатора — фиксированный лимит размера файла (например, TNM5000),
    /// либо "Пользовательские настройки" (MaxPartSizeBytes == null — значение вводит сам пользователь).
    ///
    /// Name — свойство с уведомлением, потому что у пользовательского пресета оно
    /// переводится: при смене языка ConvertViewModel обновляет его на месте. Заменять
    /// сам элемент в списке нельзя — на него может указывать SelectedPreset.
    /// </summary>
    public partial class ProgrammerPreset : ObservableObject
    {
        [ObservableProperty] private string name;

        public long? MaxPartSizeBytes { get; }
        public bool IsCustom => MaxPartSizeBytes == null;

        public ProgrammerPreset(string name, long? maxPartSizeBytes)
        {
            this.name = name;
            MaxPartSizeBytes = maxPartSizeBytes;
        }

        public override string ToString() => Name;
    }
}
