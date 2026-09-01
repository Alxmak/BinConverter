using System;
using System.Text;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Подпись под полосой прогресса: что сейчас делается, сколько осталось и с какой
    /// скоростью.
    ///
    /// Всё в одну строку и через один разделитель — отдельной строки для оценки не
    /// заводим: под полосой и так тесно, а вторая строка сдвигала бы всё, что ниже,
    /// ровно в тот момент, когда оценка появляется, и обратно, когда пропадает.
    ///
    /// Оценки может не быть — в начале работы, на паузе, у операции без известного
    /// объёма. Тогда остаётся ровно то, что было раньше: имя файла и его номер.
    /// </summary>
    public static class ProgressCaption
    {
        /// <summary>
        /// Разделитель частей. В словарь не вынесен намеренно: это знак, а не текст,
        /// и он одинаков на обоих языках.
        /// </summary>
        private const string Separator = " · ";

        private const double Kb = 1024.0;
        private const double Mb = 1024.0 * 1024;
        private const double Gb = 1024.0 * 1024 * 1024;

        /// <summary>
        /// Собирает подпись: <paramref name="what"/> — то, что показывалось и раньше;
        /// остальное добавляется, только если посчиталось.
        /// </summary>
        public static string Build(string what, double? bytesPerSecond, TimeSpan? remaining)
        {
            var text = new StringBuilder(what);

            // Порядок не случаен: «осталось» — ответ на вопрос, ради которого сюда
            // смотрят, а скорость только объясняет, откуда это число взялось.
            if (remaining is { } left)
            {
                if (text.Length > 0) text.Append(Separator);
                text.Append(Strings.Format("Common_ProgressRemaining", FormatDuration(left)));
            }

            if (bytesPerSecond is { } speed)
            {
                if (text.Length > 0) text.Append(Separator);
                text.Append(FormatSpeed(speed));
            }

            return text.ToString();
        }

        /// <summary>
        /// Время округляется вверх и до минут: секунды в оценке на час работы —
        /// ложная точность, а меняющееся каждую секунду число под полосой ещё и мешает
        /// читать всё остальное.
        /// </summary>
        public static string FormatDuration(TimeSpan value)
        {
            if (value < TimeSpan.FromMinutes(1)) return Strings.Get("Common_DurationUnderMinute");

            int minutes = (int)Math.Ceiling(value.TotalMinutes);

            // Округление вверх может само добрать до часа: 59 минут 40 секунд — это
            // «1 ч 00 мин», а не «60 мин».
            if (minutes < 60) return Strings.Format("Common_DurationMinutes", minutes);

            return Strings.Format("Common_DurationHours", minutes / 60, minutes % 60);
        }

        /// <summary>
        /// Скорость с той точностью, которая на этом порядке что-то значит: мегабайты
        /// целыми (разница между 180 и 181 МБ/с не говорит ни о чём), гигабайты — до
        /// сотых, иначе на быстром диске осталось бы «1 ГБ/с» при любой скорости.
        /// </summary>
        public static string FormatSpeed(double bytesPerSecond) =>
            bytesPerSecond >= Gb ? Strings.Format("Common_SpeedGb", bytesPerSecond / Gb)
            : bytesPerSecond >= Mb ? Strings.Format("Common_SpeedMb", bytesPerSecond / Mb)
            : Strings.Format("Common_SpeedKb", bytesPerSecond / Kb);
    }
}
