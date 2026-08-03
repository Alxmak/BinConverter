using System.Windows;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Читает путь к файлу/папке из буфера обмена для read-only полей путей —
    /// обрезает пробелы и обёртывающие кавычки (например, от "Скопировать как путь"
    /// в проводнике Windows, который оборачивает путь в кавычки).
    /// </summary>
    public static class ClipboardPathHelper
    {
        public static string? TryGetPath()
        {
            try
            {
                if (!Clipboard.ContainsText()) return null;

                string text = Clipboard.GetText().Trim();
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                    text = text[1..^1];

                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                // буфер обмена иногда занят другим процессом — не критично
                return null;
            }
        }
    }
}
