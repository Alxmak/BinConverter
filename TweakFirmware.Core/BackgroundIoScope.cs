using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Временно переводит процесс в "фоновый" режим Windows (Background Mode):
    /// снижает приоритет использования диска и процессора на время длительной
    /// операции с большим файлом, чтобы остальные программы на компьютере
    /// не подтормаживали, пока конвертер грузит диск.
    ///
    /// Использование: using var scope = new BackgroundIoScope();
    /// При выходе из using-блока приоритет автоматически возвращается к обычному.
    /// </summary>
    public sealed class BackgroundIoScope : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        private const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;
        private const uint PROCESS_MODE_BACKGROUND_END = 0x00200000;

        private bool _active;

        public BackgroundIoScope()
        {
            _active = TrySetMode(PROCESS_MODE_BACKGROUND_BEGIN);
        }

        public void Dispose()
        {
            if (!_active) return;

            TrySetMode(PROCESS_MODE_BACKGROUND_END);
            _active = false;
        }

        /// <summary>
        /// Process.GetCurrentProcess() возвращает объект, владеющий хендлом, — его нужно
        /// освобождать. Без using каждая операция оставляла после себя два незакрытых
        /// хендла (по одному на вход в режим и на выход).
        /// </summary>
        private static bool TrySetMode(uint mode)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return SetPriorityClass(process.Handle, mode);
            }
            catch
            {
                return false; // если не получилось — работаем как обычно, без фонового режима
            }
        }
    }
}
