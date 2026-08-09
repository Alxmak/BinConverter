using System;

namespace TweakFirmware.Core.Dump
{
    /// <summary>
    /// Окно кэша поверх любого читателя дампа.
    ///
    /// Нужно из-за того, как работают детекторы разметок: они щупают дамп множеством
    /// мелких чтений подряд — по несколько байт сигнатуры на каждый проверяемый адрес.
    /// Сканирование MSTAR NAND, например, проходит диапазон 0x200000…0x4000000 с шагом
    /// 0x1000, и без кэша это шестнадцать тысяч обращений к диску. Оригинал решал ту же
    /// задачу двухмегабайтным окном внутри функции чтения; здесь окно вынесено в отдельный
    /// слой, потому что оно одинаково полезно и для NAND, и для eMMC.
    ///
    /// На читаемые значения кэш не влияет никак — только на скорость.
    /// </summary>
    public sealed class CachingDumpReader : IDumpReader
    {
        public const int DefaultWindowSize = 2 * 1024 * 1024;

        private readonly IDumpReader _inner;
        private readonly bool _ownsInner;
        private readonly byte[] _window;

        /// <summary>Логический адрес начала окна; -1 — окно пусто.</summary>
        private long _windowStart = -1;

        /// <summary>Сколько байт окна заполнено данными.</summary>
        private int _windowLength;

        public CachingDumpReader(IDumpReader inner, int windowSize = DefaultWindowSize, bool ownsInner = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
            _ownsInner = ownsInner;
            _window = new byte[windowSize];
        }

        public long PhysicalSize => _inner.PhysicalSize;

        public long LogicalSize => _inner.LogicalSize;

        public NandGeometry? Geometry => _inner.Geometry;

        public int Read(long logicalOffset, Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;

            // Запрос больше окна кэшировать бессмысленно: он вытеснил бы окно целиком,
            // а следующий мелкий запрос всё равно пришёл бы мимо.
            if (destination.Length > _window.Length)
                return _inner.Read(logicalOffset, destination);

            if (logicalOffset < 0 || logicalOffset >= LogicalSize)
            {
                destination.Clear();
                return 0;
            }

            if (!InWindow(logicalOffset, destination.Length))
                FillWindow(logicalOffset);

            int start = (int)(logicalOffset - _windowStart);
            int available = Math.Max(0, _windowLength - start);
            int take = Math.Min(available, destination.Length);

            if (take > 0) _window.AsSpan(start, take).CopyTo(destination);
            if (take < destination.Length) destination[take..].Clear();
            return take;
        }

        private bool InWindow(long offset, int length) =>
            _windowStart >= 0 &&
            offset >= _windowStart &&
            offset + length <= _windowStart + _windowLength;

        private void FillWindow(long offset)
        {
            // Окно начинается ровно с запрошенного адреса: детекторы читают вперёд,
            // поэтому попадания дают именно последующие адреса, а не предыдущие.
            _windowStart = offset;
            _windowLength = _inner.Read(offset, _window);

            // Хвост дампа: прочитано меньше окна — это не ошибка, просто данные кончились.
            if (_windowLength <= 0)
            {
                _windowLength = 0;
                _windowStart = -1;
            }
        }

        public byte[] ReadBlock(long logicalOffset, int length)
        {
            var buffer = new byte[length];
            Read(logicalOffset, buffer);
            return buffer;
        }

        /// <summary>
        /// Сбрасывает окно. Вызывается, когда меняется способ чтения дампа — например,
        /// после того как детектор BLUERAY уточнил раскладку страницы: данные по тем же
        /// логическим адресам после этого другие.
        /// </summary>
        public void Reset()
        {
            _windowStart = -1;
            _windowLength = 0;
        }

        public void Dispose()
        {
            if (_ownsInner) _inner.Dispose();
        }
    }
}
