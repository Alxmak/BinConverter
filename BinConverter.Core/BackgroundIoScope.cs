using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BinConverter.Core
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
            try
            {
                _active = SetPriorityClass(Process.GetCurrentProcess().Handle, PROCESS_MODE_BACKGROUND_BEGIN);
            }
            catch
            {
                _active = false; // если не получилось — работаем как обычно, без фонового режима
            }
        }

        public void Dispose()
        {
            if (_active)
            {
                try { SetPriorityClass(Process.GetCurrentProcess().Handle, PROCESS_MODE_BACKGROUND_END); }
                catch { /* игнорируем — процесс всё равно скоро завершит операцию */ }
                _active = false;
            }
        }
    }
}
