using System;
using System.IO;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    public class DroppedFolderTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tfw-drop-" + Guid.NewGuid().ToString("N"));

        public DroppedFolderTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void Folder_IsTakenAsIs()
        {
            Assert.Equal(_root, DroppedFolder.Resolve(_root));
        }

        [Fact]
        public void File_ResolvesToItsFolder()
        {
            // Схватить файл в проводнике проще, чем прицелиться в саму папку.
            string file = Path.Combine(_root, "dump.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

            Assert.Equal(_root, DroppedFolder.Resolve(file));
        }

        [Fact]
        public void MissingPath_GivesNothing()
        {
            // Лучше не реагировать, чем подставить в поле путь, которого нет.
            Assert.Null(DroppedFolder.Resolve(Path.Combine(_root, "нет-такого", "и-файла.bin")));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankPath_GivesNothing(string? path)
        {
            Assert.Null(DroppedFolder.Resolve(path));
        }
    }
}
