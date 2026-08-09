using System;
using System.IO;

namespace TweakFirmware.Core.Dump
{
    /// <summary>
    /// Нестандартная раскладка страницы у части плееров Blu-ray (микросхемы
    /// TC58NVG2S0FTA00, TC58NVG2S0HTAI0): физическая страница состоит из нескольких
    /// подстраниц <c>main + spare</c>, за которыми может идти хвост из байт 0xFF.
    /// Определяется детектором BLUERAY по ходу разбора и после этого влияет на чтение.
    /// </summary>
    public sealed record BluerayPageLayout(int Main, int Spare, int SubpagesPerPage, int TrailingFf)
    {
        public int LogicalBytesPerPage => Main * SubpagesPerPage;

        public int PhysicalBytesPerPage => (Main + Spare) * SubpagesPerPage + TrailingFf;
    }

    /// <summary>
    /// Дамп NAND: между данными разбросаны служебные области, которые при чтении
    /// пропускаются. Смещения снаружи — логические (как если бы spare не было).
    ///
    /// Внутри всё сведено к одной модели: физическая страница описывается списком
    /// «участков данных» — пар (смещение внутри страницы, длина). Для обычной схемы
    /// участок один, для Dune HD их пять, для нестандартного Blu-ray — по числу
    /// подстраниц. Оригинальный скрипт вместо этого имел три почти одинаковых цикла
    /// последовательного чтения; они давали тот же результат, но только для смещений,
    /// выровненных по началу страницы. Здесь любое смещение читается верно.
    /// </summary>
    public sealed class NandDumpReader : IDumpReader
    {
        /// <summary>Участок полезных данных внутри физической страницы.</summary>
        private readonly record struct DataRun(int OffsetInPage, int Length);

        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly NandGeometry _geometry;

        private DataRun[] _runs = Array.Empty<DataRun>();
        private int _logicalBytesPerPage;
        private BluerayPageLayout? _blueray;

        public NandDumpReader(Stream stream, NandGeometry geometry, bool ownsStream = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek) throw new ArgumentException("Поток должен поддерживать поиск.", nameof(stream));
            _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
            _ownsStream = ownsStream;
            PhysicalSize = stream.Length;
            RebuildPageMap();
        }

        public long PhysicalSize { get; }

        public long LogicalSize => _geometry.MinusSpare(PhysicalSize);

        public NandGeometry? Geometry => _geometry;

        /// <summary>
        /// Раскладка Blu-ray, если она обнаружена. Свойство меняемое намеренно: раскладку
        /// вычисляет детектор BLUERAY, а он сам читает дамп через этого же читателя —
        /// то есть узнать её до создания читателя невозможно. Тот же порядок был и в
        /// оригинале, только через глобальную переменную.
        /// </summary>
        public BluerayPageLayout? Blueray
        {
            get => _blueray;
            set
            {
                _blueray = value;
                RebuildPageMap();
            }
        }

        /// <summary>
        /// Раскладка страницы Dune HD, 0x840 байт: метка, три блока по 512 со вставками
        /// spare по 13 байт, блок 469, шесть байт мусора, блок 43, spare и два байта мусора.
        /// Полезных данных ровно 2048 — столько же, сколько main у страницы 2048+64.
        /// </summary>
        private static readonly DataRun[] DuneHdRuns =
        {
            new(0x004, 512),
            new(0x211, 512),
            new(0x41E, 512),
            new(0x62B, 469),
            new(0x806, 43)
        };

        private void RebuildPageMap()
        {
            if (_blueray is { } bl && UseBluerayLayout(bl))
            {
                var runs = new DataRun[bl.SubpagesPerPage];
                for (int i = 0; i < bl.SubpagesPerPage; i++)
                    runs[i] = new DataRun(i * (bl.Main + bl.Spare), bl.Main);
                _runs = runs;
            }
            else
            {
                _runs = _geometry.Layout == NandPageLayout.DuneHd
                    ? DuneHdRuns
                    : new[] { new DataRun(0, _geometry.Main) };
            }

            _logicalBytesPerPage = 0;
            foreach (var run in _runs) _logicalBytesPerPage += run.Length;
        }

        /// <summary>
        /// Особый путь чтения включается только для микросхем, у которых подстраница
        /// отличается от обычной 1024+32. Условие дословно повторяет оригинал: при
        /// main = 0x400 и spare = 0x20 раскладка совпадает с обычной, и городить
        /// подстраницы незачем.
        /// </summary>
        private bool UseBluerayLayout(BluerayPageLayout bl) =>
            bl.SubpagesPerPage > 0 && (_geometry.Main != 0x400 || _geometry.Spare != 0x20);

        public int Read(long logicalOffset, Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            if (logicalOffset < 0 || logicalOffset >= LogicalSize)
            {
                destination.Clear();
                return 0;
            }

            long pageIndex = logicalOffset / _logicalBytesPerPage;
            int withinPage = (int)(logicalOffset % _logicalBytesPerPage);

            int written = 0;
            while (written < destination.Length)
            {
                long pageStart = pageIndex * _geometry.Page;
                if (pageStart >= PhysicalSize) break;

                int consumed = 0;
                foreach (var run in _runs)
                {
                    if (written >= destination.Length) break;

                    // Пропускаем участки, которые целиком левее нужного места на странице.
                    if (withinPage >= consumed + run.Length)
                    {
                        consumed += run.Length;
                        continue;
                    }

                    int skipInRun = Math.Max(0, withinPage - consumed);
                    int available = run.Length - skipInRun;
                    int take = Math.Min(available, destination.Length - written);

                    long physical = pageStart + run.OffsetInPage + skipInRun;
                    if (physical >= PhysicalSize) break;
                    if (physical + take > PhysicalSize) take = (int)(PhysicalSize - physical);
                    if (take <= 0) break;

                    ReadRaw(physical, destination.Slice(written, take));
                    written += take;
                    consumed += run.Length;
                }

                pageIndex++;
                withinPage = 0;
            }

            if (written < destination.Length) destination[written..].Clear();
            return written;
        }

        private void ReadRaw(long position, Span<byte> destination)
        {
            _stream.Position = position;
            int total = 0;
            while (total < destination.Length)
            {
                int read = _stream.Read(destination[total..]);
                if (read <= 0) break;
                total += read;
            }
            if (total < destination.Length) destination[total..].Clear();
        }

        public byte[] ReadBlock(long logicalOffset, int length)
        {
            var buffer = new byte[length];
            Read(logicalOffset, buffer);
            return buffer;
        }

        /// <summary>Читает байты как они лежат в файле, вместе со служебными областями.</summary>
        public int ReadPhysical(long physicalOffset, Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            if (physicalOffset < 0 || physicalOffset >= PhysicalSize)
            {
                destination.Clear();
                return 0;
            }

            int available = (int)Math.Min(destination.Length, PhysicalSize - physicalOffset);
            ReadRaw(physicalOffset, destination[..available]);
            if (available < destination.Length) destination[available..].Clear();
            return available;
        }

        /// <summary>Сколько целых физических страниц помещается в дампе.</summary>
        public long PageCount => PhysicalSize / _geometry.Page;

        /// <summary>
        /// Читает физическую страницу целиком — вместе со spare. Нужно операции разрезания
        /// дампа на main.bin и spare.bin; буфер передаёт вызывающий, чтобы на дампе в
        /// несколько гигабайт не выделять память на каждую из миллионов страниц.
        /// </summary>
        public void ReadRawPage(long pageIndex, Span<byte> page)
        {
            if (page.Length != _geometry.Page)
                throw new ArgumentException("Буфер должен быть размером ровно в страницу.", nameof(page));

            ReadRaw(pageIndex * _geometry.Page, page);
        }

        public void Dispose()
        {
            if (_ownsStream) _stream.Dispose();
        }
    }
}
