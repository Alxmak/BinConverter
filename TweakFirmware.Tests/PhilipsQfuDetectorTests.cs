using System;
using System.IO;
using System.Text;
using System.Threading;
using TweakFirmware.Core.Analysis;
using TweakFirmware.Core.Dump;
using TweakFirmware.Core.Partitions;
using TweakFirmware.Core.Partitions.Layouts;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Телевизоры Philips: разметка QFUx.x и микросхема настроек 24C64.
    /// </summary>
    public class PhilipsQfuDetectorTests
    {
        /// <summary>Первые восемьдесят байт машинного кода — по ним разметка и опознаётся.</summary>
        private static readonly byte[] BootCode =
        {
            0x00, 0x68, 0x80, 0x40, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x60, 0x80, 0x40, 0xC0, 0x00, 0x00, 0x00,
            0x00, 0xBB, 0x03, 0x3C, 0x08, 0x20, 0x63, 0x34, 0x00, 0x33, 0x02, 0x3C, 0x02, 0x00, 0x42, 0x34,
            0x00, 0x00, 0x62, 0xAC, 0x00, 0xB5, 0x03, 0x3C, 0x49, 0xE8, 0x63, 0x34, 0x00, 0x00, 0x02, 0x24,
            0x00, 0x00, 0x62, 0xA0, 0x00, 0xBB, 0x03, 0x3C, 0x20, 0x20, 0x63, 0x34, 0x00, 0x00, 0x02, 0x24,
            0x00, 0x00, 0x62, 0xAC, 0x00, 0xBB, 0x03, 0x3C, 0x70, 0x20, 0x63, 0x34, 0x00, 0x00, 0x02, 0x24
        };

        private static (PartitionTable Table, bool Detected, DumpContext Context, SilentAnalysisHost Host) Run(IDumpReader dump)
        {
            var context = new DumpContext(dump);
            var host = new SilentAnalysisHost();
            var table = new PartitionTable();
            var detector = new PhilipsQfuDetector();

            var detection = detector.TryDetect(dump, context, host, CancellationToken.None);
            if (detection is null) return (table, false, context, host);

            detector.ReadParts(detection, table, dump, context, host, CancellationToken.None);
            return (table, true, context, host);
        }

        [Fact]
        public void ReadsThePartitionTableWithBigEndianFields()
        {
            var dump = new SparseDumpReader(0x400000);
            dump.Put(0, BootCode);

            dump.Put(0x66000, QfuRecord("uboot", offset: 0, size: 0x100000));
            dump.Put(0x66000 + 0x1D, QfuRecord("kernel", offset: 0x100000, size: 0x200000));

            // Конец таблицы помечен единицами в поле смещения.
            dump.Put(0x66000 + 2 * 0x1D, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            var (table, detected, _, _) = Run(dump);

            Assert.True(detected);
            Assert.Equal(2, table.Count);

            Assert.Equal("uboot", table[0].Name);
            Assert.Equal(0, table[0].Offset);
            Assert.Equal(0x100000, table[0].Length);

            Assert.Equal("kernel", table[1].Name);
            Assert.Equal(0x100000, table[1].Offset);
            Assert.Equal(0x200000, table[1].Length);
        }

        [Fact]
        public void WithoutTheBootCodeDetectsNothing()
        {
            var dump = new SparseDumpReader(0x400000);
            dump.Put(0, new byte[] { 0x11, 0x22, 0x33, 0x44 });

            Assert.False(Run(dump).Detected);
        }

        [Fact]
        public void ReadsModelVersionAndSerialFromTheRwbootArea()
        {
            // Описание прошивки лежит в разделе RWBOOT далеко от начала дампа.
            var dump = new SparseDumpReader(0x24000000);
            dump.Put(0, BootCode);
            dump.Put(0x66000, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            const string Block =
                "SW version = QF2EU-0.173.65.0 Release\n" +
                "Set type   = 55PFL6158K/12\n" +
                "Set serial = ZH1H1335007420\n";

            dump.Put(0x1CC00000 + 0x800, Encoding.ASCII.GetBytes(Block));

            var (_, detected, context, host) = Run(dump);

            Assert.True(detected);
            Assert.NotNull(context.PhilipsInfo);

            Assert.Equal("55PFL6158K/12", context.PhilipsInfo!.Type);
            Assert.Equal("QF2EU-0.173.65.0 Release", context.PhilipsInfo.SwVersion);
            Assert.Equal("ZH1H1335007420", context.PhilipsInfo.Serial);

            // Косая черта в модели заменяется: в имени файла её быть не может.
            Assert.Equal("55PFL6158K_12 QF2EU-0.173.65.0 Release ZH1H1335007420.bin",
                context.PhilipsInfo.SuggestedFileName);

            Assert.Contains(host.Messages, m => m.Message.Contains("55PFL6158K/12"));
        }

        [Fact]
        public void TheSameDescriptionRepeatedManyTimesIsReportedOnce()
        {
            var dump = new SparseDumpReader(0x24000000);
            dump.Put(0, BootCode);
            dump.Put(0x66000, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            const string Block =
                "SW version = QF2EU-1.0 Release\n" +
                "Set type   = 42PFL0000/12\n" +
                "Set serial = AB1C2345678901\n";

            // В настоящем дампе описание встречается в нескольких копиях подряд.
            for (int i = 0; i < 5; i++)
                dump.Put(0x1CC00000 + 0x800 + i * 0x1000, Encoding.ASCII.GetBytes(Block));

            var (_, _, _, host) = Run(dump);

            int reported = 0;
            foreach (var message in host.Messages)
                if (message.Message.Contains("42PFL0000/12")) reported++;

            Assert.Equal(1, reported);
        }

        [Fact]
        public void MissingDescriptionIsReportedButNotFatal()
        {
            var dump = new SparseDumpReader(0x24000000);
            dump.Put(0, BootCode);
            dump.Put(0x66000, QfuRecord("uboot", offset: 0, size: 0x100000));
            dump.Put(0x66000 + 0x1D, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            var (table, detected, context, host) = Run(dump);

            Assert.True(detected);
            Assert.Single(table.Items);
            Assert.Null(context.PhilipsInfo);
            Assert.Contains(host.Messages, m => m.Level == AnalysisLogLevel.Warning);
        }

        private static byte[] QfuRecord(string name, uint offset, uint size)
        {
            var record = new byte[0x1D];

            for (int i = 0; i < 4; i++) record[i] = (byte)(offset >> ((3 - i) * 8));
            for (int i = 0; i < 4; i++) record[4 + i] = (byte)(size >> ((3 - i) * 8));

            Encoding.ASCII.GetBytes(name).CopyTo(record, 0x12);
            return record;
        }

        // ---------- 24C64 ----------

        [Fact]
        public void Eeprom24C64_ReadsModelSerialAndOptions()
        {
            var builder = new DumpBuilder(0x2000);
            builder.WriteAscii(0x20, "FUSIO");
            builder.WriteAscii(0x10F8, "55PFL6158K/12");
            builder.WriteAscii(0x1106, "ZH1H1335007420");

            builder.WriteUInt16(0x10E5, 1234);
            builder.WriteUInt16(0x10E7, 5678);

            using var dump = new PlainDumpReader(new MemoryStream(builder.Build()));

            var content = Philips24C64.TryRead(dump);

            Assert.NotNull(content);
            Assert.Equal("55PFL6158K/12", content!.Model);
            Assert.Equal("ZH1H1335007420", content.Serial);
            Assert.Equal(1234, content.Options[0]);
            Assert.Equal(5678, content.Options[1]);
            Assert.Equal("55PFL6158K_12 ZH1H1335007420 24C64.bin", content.SuggestedFileName);
        }

        [Fact]
        public void Eeprom24C64_OnlyEightKilobyteFilesQualify()
        {
            var builder = new DumpBuilder(0x4000);
            builder.WriteAscii(0x20, "FUSIO");

            using var dump = new PlainDumpReader(new MemoryStream(builder.Build()));

            Assert.Null(Philips24C64.TryRead(dump));
        }

        [Fact]
        public void Eeprom24C64_WithoutTheMarkerIsNotOne()
        {
            using var dump = new PlainDumpReader(new MemoryStream(new DumpBuilder(0x2000).Build()));

            Assert.Null(Philips24C64.TryRead(dump));
        }
    }
}
