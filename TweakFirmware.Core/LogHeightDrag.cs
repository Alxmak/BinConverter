using System;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Пересчёт высоты журнала, пока тянут ручку под ним: из смещения мыши от точки
    /// захвата получается число видимых строк.
    ///
    /// Счёт вынесен из карточки, потому что проверяется он только числами. Ошибка
    /// здесь не выглядит ошибкой в вычислениях: на экране это дрожание — журнал
    /// скачет между двумя размерами, пока мышь ровно едет в одну сторону. Разбирать
    /// такое по виду бесполезно, а прогоном ряда смещений ловится сразу.
    /// </summary>
    public sealed class LogHeightDrag
    {
        /// <summary>
        /// За сколько строки от нынешнего размера журнал переключается на соседний.
        /// Больше половины намеренно: ровно на половине дрожание мыши в один пиксель
        /// перекидывало бы журнал между двумя размерами и обратно.
        /// </summary>
        private const double SwitchThreshold = 0.6;

        private readonly double _rowHeight;
        private readonly int _minLines;
        private readonly int _maxLines;

        /// <summary>
        /// Высота, отвечающая нулевому смещению мыши. Не постоянна: ход, упёршийся
        /// в границу, из неё вычитается — см. <see cref="LinesFor"/>.
        /// </summary>
        private double _anchorHeight;

        /// <summary>Размер, выданный в прошлый раз: от него отсчитывается порог переключения.</summary>
        private int _lines;

        /// <param name="startHeight">Высота журнала на момент захвата ручки, в пикселях.</param>
        /// <param name="rowHeight">Высота одной строки списка, в пикселях.</param>
        public LogHeightDrag(double startHeight, double rowHeight, int minLines, int maxLines)
        {
            if (rowHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(rowHeight), rowHeight, "Высота строки должна быть больше нуля.");
            if (minLines < 1)
                throw new ArgumentOutOfRangeException(nameof(minLines), minLines, "Строк не может быть меньше одной.");
            if (maxLines < minLines)
                throw new ArgumentOutOfRangeException(nameof(maxLines), maxLines, "Верхняя граница ниже нижней.");

            _rowHeight = rowHeight;
            _minLines = minLines;
            _maxLines = maxLines;
            _anchorHeight = startHeight;
            _lines = ToLines(Math.Clamp(startHeight, minLines * rowHeight, maxLines * rowHeight));
        }

        /// <param name="mouseOffset">
        /// Смещение мыши от точки захвата ручки, в пикселях; вниз — положительное.
        /// Именно от точки захвата, а не от прошлого вызова: складывать приходящие
        /// подряд смещения нельзя, они и так накопительные.
        /// </param>
        /// <returns>Сколько строк журнала показывать.</returns>
        public int LinesFor(double mouseOffset)
        {
            double target = _anchorHeight + mouseOffset;
            double clamped = Math.Clamp(target, _minLines * _rowHeight, _maxLines * _rowHeight);

            // Ход, упёршийся в границу, забываем. Иначе мышь, уведённая далеко вниз,
            // копила бы «долг» в десятки пикселей, и обратный ход начинал двигать
            // журнал не сразу, а только когда долг отработан.
            _anchorHeight += clamped - target;

            if (Math.Abs(clamped - _lines * _rowHeight) > _rowHeight * SwitchThreshold)
                _lines = ToLines(clamped);

            return _lines;
        }

        /// <summary>Высота в пикселях — в число строк, с оглядкой на границы.</summary>
        private int ToLines(double height) =>
            Math.Clamp((int)Math.Round(height / _rowHeight), _minLines, _maxLines);
    }
}
