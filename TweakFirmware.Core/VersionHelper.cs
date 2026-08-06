using System;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Сравнение версий приложения. Раньше эта логика была написана дважды: нормализация
    /// строки жила и в UpdateService.TryParseVersion, и в UpdateManager.NormalizeVersion,
    /// причём второй вариант разбирал текущую версию через конструктор Version, который
    /// на неразборчивой строке бросает исключение вместо возврата false.
    ///
    /// Ошибка здесь тихая и потому неприятная: сравнение просто перестанет считать новую
    /// версию новее, обновления не будут предлагаться, и никто этого не заметит.
    /// Поэтому логика одна и покрыта тестами.
    /// </summary>
    public static class VersionHelper
    {
        /// <summary>
        /// Разбирает версию, дополняя недостающие части нулями: Version.TryParse требует
        /// минимум "Major.Minor", а тег релиза может быть и "3", и "2.6".
        /// </summary>
        public static bool TryParse(string? text, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] parts = text.Split('.');
            string normalized = parts.Length switch
            {
                1 => $"{text}.0.0",
                2 => $"{text}.0",
                _ => text
            };

            // Присваиваем только при успехе: Version.TryParse на неудаче обнуляет out-параметр,
            // а тип его null не допускает — вызывающий код вправе на это рассчитывать.
            if (!Version.TryParse(normalized, out var parsed)) return false;

            version = parsed;
            return true;
        }

        /// <summary>
        /// Строго новее ли <paramref name="candidate"/>, чем <paramref name="current"/>.
        /// Если хотя бы одна из строк не разбирается — false: лучше не предложить
        /// обновление, чем предложить установку неизвестно чего.
        /// </summary>
        public static bool IsNewer(string? candidate, string? current) =>
            TryParse(candidate, out var candidateVersion) &&
            TryParse(current, out var currentVersion) &&
            candidateVersion > currentVersion;
    }
}
