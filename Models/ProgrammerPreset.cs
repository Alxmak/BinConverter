namespace BinConverter.Models
{
    /// <summary>
    /// Пресет программатора — фиксированный лимит размера файла (например, TNM5000),
    /// либо "Пользовательские настройки" (MaxPartSizeBytes == null — значение вводит сам пользователь).
    /// </summary>
    public class ProgrammerPreset
    {
        public string Name { get; }
        public long? MaxPartSizeBytes { get; }
        public bool IsCustom => MaxPartSizeBytes == null;

        public ProgrammerPreset(string name, long? maxPartSizeBytes)
        {
            Name = name;
            MaxPartSizeBytes = maxPartSizeBytes;
        }

        public override string ToString() => Name;
    }
}
