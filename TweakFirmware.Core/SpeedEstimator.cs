using System;
using System.Collections.Generic;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Скорость работы и оставшееся время по ходу операции.
    ///
    /// Полоса прогресса отвечает на вопрос «сколько сделано», но не на тот, ради которого
    /// на неё смотрят: успею ли я сходить за кофе или это до вечера. На дампе в восемь
    /// гигабайт разница между «десять минут» и «полтора часа» решает, ждать ли у экрана,
    /// а по одним процентам её видно только если стоять и считать самому.
    ///
    /// Считается по скользящему окну, а не от начала работы: скорость записи меняется —
    /// первые секунды уходят на раскачку, сетевой диск проседает, кэш системы то принимает
    /// поток целиком, то нет. Среднее от начала операции такие изменения сглаживает
    /// намертво и к концу часовой работы врёт заметнее, чем в начале.
    ///
    /// Часы сюда передаются снаружи, отдельным значением: так оценку можно прогнать
    /// на ряде отсчётов в тесте, не дожидаясь настоящих секунд.
    /// </summary>
    public sealed class SpeedEstimator
    {
        /// <summary>Сколько последних секунд усредняем.</summary>
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Короче этого промежутка скорость не считаем: на долях секунды она скачет
        /// в разы, и показывать такое — только мельтешить числами.
        /// </summary>
        private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Разрыв, после которого прежние отсчёты уже ни о чём не говорят: столько
        /// времени подряд без единого отсчёта — это пауза или ожидание диска, а не
        /// медленная работа. Усреднить его вместе с работой значит показать скорость
        /// вдвое ниже настоящей, и ещё на всё окно усреднения вперёд.
        /// </summary>
        private static readonly TimeSpan Gap = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Оценка длиннее суток — не оценка, а признак того, что скорость ещё не устоялась
        /// (например, первые байты пошли, а полоса ещё около нуля). Лучше не показывать
        /// ничего, чем «осталось 940 ч».
        /// </summary>
        private static readonly TimeSpan Unbelievable = TimeSpan.FromDays(1);

        private readonly List<(TimeSpan At, long Done)> _samples = new();

        /// <summary>Очередной отсчёт: сколько прошло от начала работы и сколько сделано.</summary>
        public void Add(TimeSpan elapsed, long done)
        {
            if (_samples.Count > 0)
            {
                var last = _samples[^1];

                // Время назад не идёт, и сделанного не убывает: такое бывает только при
                // повторном использовании оценщика без сброса. Начинаем считать заново.
                if (elapsed < last.At || done < last.Done || elapsed - last.At > Gap) _samples.Clear();
            }

            _samples.Add((elapsed, done));

            // Всё, что старше окна, только замедляет отклик на изменение скорости.
            // Два последних отсчёта не выбрасываем никогда: без них считать нечего.
            int drop = 0;
            while (drop < _samples.Count - 2 && elapsed - _samples[drop].At > Window) drop++;
            if (drop > 0) _samples.RemoveRange(0, drop);
        }

        /// <summary>Забыть накопленное: работа началась заново.</summary>
        public void Reset() => _samples.Clear();

        /// <summary>
        /// Байт в секунду или <c>null</c>, если считать ещё рано (мало отсчётов, слишком
        /// короткий промежуток) либо не по чему (за окно не сделано ни байта — так
        /// выглядит пауза).
        /// </summary>
        public double? BytesPerSecond
        {
            get
            {
                if (_samples.Count < 2) return null;

                var first = _samples[0];
                var last = _samples[^1];

                double seconds = (last.At - first.At).TotalSeconds;
                long bytes = last.Done - first.Done;

                if (seconds < MinSpan.TotalSeconds || bytes <= 0) return null;

                return bytes / seconds;
            }
        }

        /// <summary>
        /// Сколько ещё осталось при нынешней скорости, или <c>null</c>, если сказать
        /// нечего.
        /// </summary>
        public TimeSpan? Remaining(long totalBytes)
        {
            if (_samples.Count == 0) return null;
            if (BytesPerSecond is not { } speed || speed <= 0) return null;

            long left = totalBytes - _samples[^1].Done;
            if (left <= 0) return null;

            double seconds = left / speed;
            if (seconds > Unbelievable.TotalSeconds) return null;

            return TimeSpan.FromSeconds(seconds);
        }
    }
}
