using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using TweakFirmware.Core.Operations;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Ровность цепочки по размерам частей. Проверка нужна ради одного случая, который
    /// по именам не отличить: часть скопировалась не полностью — файл на месте, имя
    /// верное, а внутри половина.
    /// </summary>
    public class ChainConsistencyTests : IDisposable
    {
        private readonly string _tempDir;

        public ChainConsistencyTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "TweakFirmwareChain_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* временные файлы — не критично, если не удалились с первого раза */ }
        }

        private string Make(string name, int size)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        /// <summary>Нарезка этой программы: все части по лимиту, последняя короче.</summary>
        [Fact]
        public void EvenChainWithShorterTail_IsNotReported()
        {
            var chain = new[] { Make("emmc.bin", 100), Make("emmc.bin.part1", 100), Make("emmc.bin.part2", 40) };

            Assert.Null(ChainConsistency.FindUnevenPartNumber(chain));
        }

        /// <summary>Размер дампа кратен лимиту — последняя часть тоже полная.</summary>
        [Fact]
        public void EvenChainWithFullTail_IsNotReported()
        {
            var chain = new[] { Make("emmc.bin", 100), Make("emmc.bin.part1", 100), Make("emmc.bin.part2", 100) };

            Assert.Null(ChainConsistency.FindUnevenPartNumber(chain));
        }

        /// <summary>
        /// Середина цепочки короче остальных — тот самый недокопированный файл.
        /// Номер в ответе такой же, как видит человек: 1 — базовый файл.
        /// </summary>
        [Fact]
        public void TruncatedMiddlePart_IsReportedByItsNumber()
        {
            var chain = new[]
            {
                Make("emmc.bin", 100),
                Make("emmc.bin.part1", 60),   // не докопировался
                Make("emmc.bin.part2", 100),
                Make("emmc.bin.part3", 40)
            };

            Assert.Equal(2, ChainConsistency.FindUnevenPartNumber(chain));
        }

        /// <summary>Хвост длиннее полной части — это уже не остаток, а чужой файл.</summary>
        [Fact]
        public void TailLongerThanFullPart_IsReported()
        {
            var chain = new[] { Make("emmc.bin", 100), Make("emmc.bin.part1", 100), Make("emmc.bin.part2", 250) };

            Assert.Equal(3, ChainConsistency.FindUnevenPartNumber(chain));
        }

        /// <summary>Одному файлу и паре «файл + хвост» сравнивать себя не с чем.</summary>
        [Fact]
        public void ShortChains_AreNeverReported()
        {
            var single = new[] { Make("single.bin", 100) };
            var pair = new[] { Make("pair.bin", 100), Make("pair.bin.part1", 10) };

            Assert.Null(ChainConsistency.FindUnevenPartNumber(single));
            Assert.Null(ChainConsistency.FindUnevenPartNumber(pair));
        }

        /// <summary>
        /// Любой лимит нарезки даёт ровную цепочку: проверка сравнивает части между
        /// собой, а не с «правильным» числом. Иначе пользовательский размер части
        /// приводил бы к предупреждению на здоровой цепочке.
        /// </summary>
        [Theory]
        [InlineData(1024)]
        [InlineData(7777)]
        [InlineData(1_000_000)]
        public async Task OurOwnSplit_NeverWarns_WhateverTheLimit(int limit)
        {
            string source = Path.Combine(_tempDir, $"src{limit}.bin");
            File.WriteAllBytes(source, new byte[limit * 2 + limit / 3]);

            string outFolder = Path.Combine(_tempDir, $"out{limit}");
            await FileSplitter.SplitAsync(source, outFolder, "emmc.bin", limit,
                verifyHash: false, progress: null, log: _ => { }, ct: CancellationToken.None);

            var chain = HashHelper.FindPartChain(Path.Combine(outFolder, "emmc.bin"));

            Assert.Equal(3, chain.Count);
            Assert.Null(ChainConsistency.FindUnevenPartNumber(chain));
        }

        /// <summary>Осмотр цепочки для предпросмотра доносит находку до интерфейса.</summary>
        [Fact]
        public void ChainProbe_PassesTheWarningToTheInterface()
        {
            Make("probe.bin", 100);
            Make("probe.bin.part1", 60);
            Make("probe.bin.part2", 100);
            Make("probe.bin.part3", 40);

            var result = ChainProbe.Measure(Path.Combine(_tempDir, "probe.bin"));

            Assert.True(result.Resolved);
            Assert.Equal(2, result.UnevenPartNumber);
        }

        /// <summary>Ровная цепочка не должна давать предупреждения в предпросмотре.</summary>
        [Fact]
        public void ChainProbe_SaysNothingAboutEvenChain()
        {
            Make("even.bin", 100);
            Make("even.bin.part1", 100);
            Make("even.bin.part2", 30);

            var result = ChainProbe.Measure(Path.Combine(_tempDir, "even.bin"));

            Assert.True(result.Resolved);
            Assert.Null(result.UnevenPartNumber);
        }
    }
}
