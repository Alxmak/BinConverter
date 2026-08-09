using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using TweakFirmware.Core.Dump;

namespace TweakFirmware.Core.FileSystems
{
    /// <summary>Запись каталога ext4.</summary>
    public sealed record Ext4Entry(string Name, uint Inode, byte Type)
    {
        /// <summary>Каталог.</summary>
        public bool IsDirectory => Type == 0x02;

        /// <summary>Обычный файл.</summary>
        public bool IsFile => Type == 0x01;
    }

    /// <summary>
    /// Минимальный обход каталогов ext4 — ровно столько, чтобы найти в томе файл и
    /// прочитать его.
    ///
    /// Нужен для одного: достать из прошивки build.prop и показать, что за Android
    /// в дампе. Полноценным читателем ext4 это не является и не пытается быть.
    ///
    /// Главное ограничение: файл считается непрерывным начиная с первого экстента.
    /// Для build.prop и прочих мелких файлов это верно всегда, для фрагментированных —
    /// нет. То же ограничение было и в оригинале.
    /// </summary>
    public sealed class Ext4DirectoryReader
    {
        /// <summary>Магическое число заголовка экстентов внутри иноды.</summary>
        private const ushort ExtentMagic = 0xF30A;

        /// <summary>Номер иноды корневого каталога — в ext4 он всегда второй.</summary>
        private const uint RootInode = 2;

        /// <summary>Длина записи дескриптора группы блоков.</summary>
        private const int GroupDescriptorLength = 0x20;

        /// <summary>
        /// Предел на размер читаемого каталога. Дальше первого экстента чтение всё равно
        /// не заходит, а бессмысленно большое число в иноде испорченного тома иначе
        /// привело бы к попытке выделить столько памяти.
        /// </summary>
        private const int MaxDirectoryBytes = 1 << 20;

        /// <summary>Предел на размер читаемого файла — build.prop на порядки меньше.</summary>
        private const int MaxFileBytes = 8 << 20;

        private readonly IDumpReader _dump;
        private readonly Ext4Superblock _superblock;
        private readonly List<uint> _inodeTableBlocks = new();

        private Ext4DirectoryReader(IDumpReader dump, Ext4Superblock superblock)
        {
            _dump = dump;
            _superblock = superblock;
        }

        /// <summary>
        /// Открывает том для обхода или возвращает <c>null</c>, если в нём не за что
        /// зацепиться: нет дескрипторов групп или размер иноды бессмысленный.
        /// </summary>
        public static Ext4DirectoryReader? Open(IDumpReader dump, Ext4Superblock superblock)
        {
            if (superblock.InodeSize is < 128 or > 4096) return null;
            if (superblock.InodesPerGroup == 0) return null;

            var reader = new Ext4DirectoryReader(dump, superblock);
            reader.ReadGroupDescriptors();

            return reader._inodeTableBlocks.Count > 0 ? reader : null;
        }

        /// <summary>
        /// Дескрипторы групп блоков лежат в блоке сразу за суперблоком. Из каждого нужен
        /// один номер — блока, где начинается таблица инод этой группы.
        /// </summary>
        private void ReadGroupDescriptors()
        {
            long at = _superblock.Offset + _superblock.BlockSize;
            int length = (int)Math.Min(_superblock.BlockSize, _dump.LogicalSize - at);
            if (length <= 0) return;

            var block = _dump.ReadBlock(at, length);

            for (int record = 0; record + GroupDescriptorLength <= block.Length; record += GroupDescriptorLength)
            {
                uint tableBlock = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(record + 0x08));

                // Нулевой номер означает, что группы кончились.
                if (tableBlock == 0) break;

                _inodeTableBlocks.Add(tableBlock);
            }
        }

        /// <summary>Где в томе лежат данные иноды и сколько их.</summary>
        private (long Offset, long Length)? ReadInode(uint inode)
        {
            if (inode == 0) return null;

            // Номера инод считаются с единицы, а места в таблице — с нуля.
            uint index = inode - 1;
            int group = (int)(index / _superblock.InodesPerGroup);
            uint indexInGroup = index % _superblock.InodesPerGroup;

            if (group >= _inodeTableBlocks.Count) return null;

            long at = _superblock.Offset
                      + _inodeTableBlocks[group] * _superblock.BlockSize
                      + (long)indexInGroup * _superblock.InodeSize;

            if (at < 0 || at + _superblock.InodeSize > _dump.LogicalSize) return null;

            var entry = _dump.ReadBlock(at, _superblock.InodeSize);

            // Данные лежат экстентами; годятся только иноды, у которых заголовок на месте.
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(0x28)) != ExtentMagic) return null;

            long size = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0x04));
            size |= (long)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0x6C)) << 32;

            // Первый экстент: с какого блока тома начинаются данные.
            uint firstBlock = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(0x3C));

            return (firstBlock * _superblock.BlockSize, size);
        }

        /// <summary>Содержимое корневого каталога тома.</summary>
        public IReadOnlyList<Ext4Entry> ReadRoot() => ReadDirectory(RootInode);

        /// <summary>Содержимое каталога по номеру его иноды.</summary>
        public IReadOnlyList<Ext4Entry> ReadDirectory(uint inode)
        {
            var result = new List<Ext4Entry>();

            var location = ReadInode(inode);
            if (location is null) return result;

            int length = (int)Math.Clamp(location.Value.Length, 0, MaxDirectoryBytes);
            if (length <= 0) return result;

            long at = _superblock.Offset + location.Value.Offset;
            if (at < 0 || at >= _dump.LogicalSize) return result;

            length = (int)Math.Min(length, _dump.LogicalSize - at);
            var data = _dump.ReadBlock(at, length);

            int position = 0;
            while (position + 8 <= data.Length)
            {
                uint entryInode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
                ushort recordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4));
                byte nameLength = data[position + 6];
                byte type = data[position + 7];

                // Нулевая длина записи означала бы вечный цикл — на этом разбор кончается.
                if (recordLength < 8) break;
                if (position + 8 + nameLength > data.Length) break;

                if (entryInode != 0 && nameLength > 0)
                {
                    string name = Encoding.UTF8.GetString(data, position + 8, nameLength);
                    result.Add(new Ext4Entry(name, entryInode, type));
                }

                position += recordLength;
            }

            return result;
        }

        /// <summary>Ищет запись по имени в заданном каталоге.</summary>
        public Ext4Entry? Find(IReadOnlyList<Ext4Entry> directory, string name)
        {
            foreach (var entry in directory)
                if (entry.Name == name) return entry;

            return null;
        }

        /// <summary>Читает содержимое файла целиком. Возвращает <c>null</c>, если не вышло.</summary>
        public byte[]? ReadFile(uint inode)
        {
            var location = ReadInode(inode);
            if (location is null) return null;

            int length = (int)Math.Clamp(location.Value.Length, 0, MaxFileBytes);
            if (length <= 0) return null;

            long at = _superblock.Offset + location.Value.Offset;
            if (at < 0 || at >= _dump.LogicalSize) return null;

            length = (int)Math.Min(length, _dump.LogicalSize - at);
            return _dump.ReadBlock(at, length);
        }
    }
}
