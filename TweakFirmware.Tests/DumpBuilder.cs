using System;
using System.Collections.Generic;
using System.Text;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Собирает синтетический дамп прошивки для тестов.
    ///
    /// Настоящие дампы телевизоров в репозиторий не положить: это гигабайты чужой
    /// прошивки. Вместо них здесь строится минимальный образ, в котором лежит ровно то,
    /// что ищет проверяемый детектор, — сигнатура по нужному адресу, таблица разделов,
    /// суперблок файловой системы. Такой образ занимает мегабайты, собирается за
    /// миллисекунды и, в отличие от настоящего дампа, точно известен наизусть.
    ///
    /// <see cref="AsNand"/> превращает готовый логический образ в физический, вставляя
    /// служебные области. Благодаря этому один и тот же тест проверяет и детектор,
    /// и правильность пересчёта адресов.
    /// </summary>
    internal sealed class DumpBuilder
    {
        private readonly byte[] _data;

        public DumpBuilder(long size, byte fill = 0x00)
        {
            if (size <= 0 || size > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
            _data = new byte[size];
            if (fill != 0x00) Array.Fill(_data, fill);
        }

        public int Size => _data.Length;

        public DumpBuilder Write(long offset, params byte[] bytes)
        {
            bytes.CopyTo(_data, offset);
            return this;
        }

        public DumpBuilder WriteAscii(long offset, string text)
        {
            Encoding.ASCII.GetBytes(text).CopyTo(_data, offset);
            return this;
        }

        public DumpBuilder WriteUInt16(long offset, ushort value)
        {
            _data[offset] = (byte)value;
            _data[offset + 1] = (byte)(value >> 8);
            return this;
        }

        public DumpBuilder WriteUInt32(long offset, uint value)
        {
            for (int i = 0; i < 4; i++) _data[offset + i] = (byte)(value >> (i * 8));
            return this;
        }

        public DumpBuilder WriteUInt64(long offset, ulong value)
        {
            for (int i = 0; i < 8; i++) _data[offset + i] = (byte)(value >> (i * 8));
            return this;
        }

        public DumpBuilder Fill(long offset, int length, byte value)
        {
            Array.Fill(_data, value, (int)offset, length);
            return this;
        }

        /// <summary>
        /// Заполняет область повторяемым узором — чтобы в тесте было видно сдвиги на
        /// любое число байт. Значение 0xFF из узора исключено намеренно: по его
        /// количеству определяется размер страницы NAND, и данные не должны это ломать.
        /// </summary>
        public DumpBuilder Pattern(long offset, int length, byte seed = 1)
        {
            for (int i = 0; i < length; i++)
                _data[offset + i] = (byte)((seed + i) % 0xFF);
            return this;
        }

        public byte[] Build() => (byte[])_data.Clone();

        /// <summary>
        /// Разворачивает логический образ в физический дамп NAND: после каждых
        /// <paramref name="main"/> байт данных вставляется <paramref name="spare"/>
        /// служебных байт. Содержимое spare задаётся отдельно, чтобы тест мог отличить
        /// служебные байты от данных.
        /// </summary>
        public static byte[] AsNand(byte[] logical, int main, int spare, byte spareFill = 0xFF)
        {
            int pages = (logical.Length + main - 1) / main;
            var physical = new byte[(long)pages * (main + spare)];

            for (int page = 0; page < pages; page++)
            {
                int sourceStart = page * main;
                int copy = Math.Min(main, logical.Length - sourceStart);
                int destStart = page * (main + spare);

                Array.Copy(logical, sourceStart, physical, destStart, copy);
                Array.Fill(physical, spareFill, destStart + main, spare);
            }

            return physical;
        }

        /// <summary>
        /// Разворачивает логический образ в дамп со страницами Dune HD (0x840 байт,
        /// 2048 полезных). Раскладка повторяет ту, что читает <see cref="NandDumpReader"/>.
        /// </summary>
        public static byte[] AsDuneHd(byte[] logical, byte spareFill = 0xEE, uint marker = 0xAAAAAAAA)
        {
            var runs = new (int OffsetInPage, int Length)[]
            {
                (0x004, 512), (0x211, 512), (0x41E, 512), (0x62B, 469), (0x806, 43)
            };

            const int PhysicalPage = 0x840;
            const int LogicalPage = 2048;

            int pages = (logical.Length + LogicalPage - 1) / LogicalPage;
            var physical = new byte[(long)pages * PhysicalPage];
            Array.Fill(physical, spareFill);

            for (int page = 0; page < pages; page++)
            {
                int pageStart = page * PhysicalPage;
                for (int i = 0; i < 4; i++) physical[pageStart + i] = (byte)(marker >> (i * 8));

                int consumed = 0;
                foreach (var run in runs)
                {
                    int sourceStart = page * LogicalPage + consumed;
                    int copy = Math.Clamp(logical.Length - sourceStart, 0, run.Length);
                    if (copy > 0) Array.Copy(logical, sourceStart, physical, pageStart + run.OffsetInPage, copy);
                    consumed += run.Length;
                }
            }

            return physical;
        }

        /// <summary>
        /// Размер файла, который <see cref="NandGeometryDetector.TryDetectRatio"/> опознает
        /// как NAND с заданным соотношением. Нужен тестам, чтобы не подбирать числа руками.
        /// </summary>
        public static long NandFileSize(int relativeMain, int relativeSpare, int pages)
        {
            if ((pages & (pages - 1)) != 0) throw new ArgumentException("Число страниц должно быть степенью двойки.", nameof(pages));
            return (long)(relativeMain + relativeSpare) * pages;
        }

        /// <summary>Все соотношения, которые понимает первый шаг определения геометрии.</summary>
        public static IEnumerable<(int RelativeMain, int RelativeSpare)> KnownRatios()
        {
            yield return (0x010, 0x01);
            yield return (0x020, 0x01);
            yield return (0x040, 0x05);
            yield return (0x080, 0x07);
            yield return (0x100, 0x0F);
        }
    }
}
