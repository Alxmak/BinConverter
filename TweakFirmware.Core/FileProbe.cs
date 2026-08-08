using System;
using System.IO;

namespace TweakFirmware.Core
{
    /// <summary>Что удалось узнать о файле, не открывая его.</summary>
    public readonly struct FileProbeResult
    {
        public bool Exists { get; init; }
        public long SizeBytes { get; init; }
    }

    /// <summary>
    /// Один вопрос к файловой системе: есть ли файл и какого он размера.
    ///
    /// Отдельно от предпросмотра во ViewModel по двум причинам. Во-первых, это
    /// единственное обращение к диску, которое там было, — вынеся его, весь предпросмотр
    /// можно считать в фоне, а раньше он шёл в потоке интерфейса на каждое нажатие
    /// клавиши в поле пути (на сетевом пути это подвешивало окно). Во-вторых, здесь
    /// собраны все способы, которыми такой вопрос может закончиться исключением:
    /// путь с недопустимыми символами, слишком длинный путь, отсутствие прав,
    /// отключившийся сетевой диск.
    /// </summary>
    public static class FileProbe
    {
        /// <summary>
        /// Никогда не бросает: непригодный путь и недоступный файл — это просто
        /// «файла нет», отдельного сообщения об этом предпросмотру не нужно.
        /// </summary>
        public static FileProbeResult Measure(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return default;

            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileProbeResult { Exists = true, SizeBytes = info.Length }
                    : default;
            }
            catch
            {
                return default;
            }
        }
    }
}
