using System;
using System.Collections.Generic;
using System.Text;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Дамп, которого нет на диске: заявленный размер любой, а данные лежат только там,
    /// где их положили.
    ///
    /// Нужен для разметок Sony. Их опознание начинается с проверки размера дампа —
    /// от двенадцати до шестнадцати гигабайт, — а сами опорные точки разбросаны по всему
    /// объёму. Настоящий файл такого размера в тестах не создать: в памяти он не
    /// поместится, а разреженный файл на NTFS, где идёт сборка, займёт место по-настоящему.
    /// Здесь же вся проверка стоит несколько килобайт.
    /// </summary>
    internal sealed class SparseDumpReader : IDumpReader
    {
        private readonly List<(long Offset, byte[] Data)> _regions = new();

        public SparseDumpReader(long size)
        {
            PhysicalSize = size;
        }

        public long PhysicalSize { get; }

        public long LogicalSize => PhysicalSize;

        public NandGeometry? Geometry => null;

        public SparseDumpReader Put(long offset, byte[] data)
        {
            _regions.Add((offset, data));
            return this;
        }

        /// <summary>Кладёт суперблок ext4 так, как его увидит проверка сигнатуры.</summary>
        public SparseDumpReader PutExt4(long offset, uint blockCount = 0, uint logBlockSize = 2)
        {
            var superblock = new byte[0x400];

            // Магическое число суперблока — по смещению 0x38 от его начала.
            superblock[0x38] = 0x53;
            superblock[0x39] = 0xEF;

            for (int i = 0; i < 4; i++) superblock[0x04 + i] = (byte)(blockCount >> (i * 8));
            for (int i = 0; i < 4; i++) superblock[0x18 + i] = (byte)(logBlockSize >> (i * 8));

            return Put(offset + 0x400, superblock);
        }

        public SparseDumpReader PutFat16(long offset)
        {
            var head = new byte[0x60];
            Encoding.ASCII.GetBytes("FAT16").CopyTo(head, 0x36);
            return Put(offset, head);
        }

        public SparseDumpReader PutAvb(long offset)
        {
            var head = new byte[0x10];
            Encoding.ASCII.GetBytes("AVB0").CopyTo(head, 0);
            return Put(offset, head);
        }

        public int Read(long logicalOffset, Span<byte> destination)
        {
            destination.Clear();

            if (logicalOffset < 0 || logicalOffset >= PhysicalSize) return 0;

            long end = Math.Min(logicalOffset + destination.Length, PhysicalSize);
            int available = (int)(end - logicalOffset);

            foreach (var (regionOffset, data) in _regions)
            {
                long overlapStart = Math.Max(logicalOffset, regionOffset);
                long overlapEnd = Math.Min(end, regionOffset + data.Length);
                if (overlapEnd <= overlapStart) continue;

                data.AsSpan((int)(overlapStart - regionOffset), (int)(overlapEnd - overlapStart))
                    .CopyTo(destination[(int)(overlapStart - logicalOffset)..]);
            }

            return available;
        }

        /// <summary>Служебных областей здесь нет, поэтому физический адрес равен логическому.</summary>
        public int ReadPhysical(long physicalOffset, Span<byte> destination) => Read(physicalOffset, destination);

        public byte[] ReadBlock(long logicalOffset, int length)
        {
            var buffer = new byte[length];
            Read(logicalOffset, buffer);
            return buffer;
        }

        public void Dispose() { }
    }
}
