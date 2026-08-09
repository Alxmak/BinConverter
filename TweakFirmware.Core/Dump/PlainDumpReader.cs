using System;
using System.IO;

namespace TweakFirmware.Core.Dump
{
    /// <summary>
    /// Дамп без служебных областей: eMMC, SD, NOR, а также любой файл, который
    /// пользователь попросил разбирать как сплошной. Логический адрес равен физическому.
    /// </summary>
    public sealed class PlainDumpReader : IDumpReader
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;

        public PlainDumpReader(Stream stream, bool ownsStream = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek) throw new ArgumentException("Поток должен поддерживать поиск.", nameof(stream));
            _ownsStream = ownsStream;
            PhysicalSize = stream.Length;
        }

        public static PlainDumpReader Open(string path) =>
            new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

        public long PhysicalSize { get; }

        public long LogicalSize => PhysicalSize;

        public NandGeometry? Geometry => null;

        public int Read(long logicalOffset, Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            if (logicalOffset < 0 || logicalOffset >= PhysicalSize)
            {
                destination.Clear();
                return 0;
            }

            _stream.Position = logicalOffset;

            int total = 0;
            while (total < destination.Length)
            {
                int read = _stream.Read(destination[total..]);
                if (read <= 0) break;
                total += read;
            }

            if (total < destination.Length) destination[total..].Clear();
            return total;
        }

        public byte[] ReadBlock(long logicalOffset, int length)
        {
            var buffer = new byte[length];
            Read(logicalOffset, buffer);
            return buffer;
        }

        public void Dispose()
        {
            if (_ownsStream) _stream.Dispose();
        }
    }
}
