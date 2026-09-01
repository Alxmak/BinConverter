using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Мигание кнопкой в панели задач — «работа закончилась, вернитесь».
    ///
    /// Операция на час заканчивалась модальным окном с итогом. Показать его программа
    /// показывала, но если окно свёрнуто, а человек занят чем-то другим — а он занят,
    /// иначе зачем сворачивать, — окно висело незамеченным, и работа считалась идущей
    /// ещё столько же. Мигание кнопки и есть тот способ позвать, который в Windows
    /// для этого предусмотрен: не поверх всего, не звуком, а отметкой в панели задач.
    ///
    /// Своего API у WPF для этого нет, поэтому FlashWindowEx напрямую.
    /// </summary>
    internal static class WindowAttention
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint TimeoutMs;
        }

        /// <summary>Мигать и заголовком, и кнопкой в панели задач.</summary>
        private const uint FlashAll = 0x00000003;

        /// <summary>Мигать, пока окно не выйдет на передний план.</summary>
        private const uint FlashUntilForeground = 0x0000000C;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        /// <summary>
        /// Позвать к окну, если человек сейчас смотрит не на него. Активное окно
        /// не мигает: звать туда, куда и так смотрят, — только раздражать.
        /// </summary>
        public static void CallIfHidden(Window? window)
        {
            if (window is null || window.IsActive) return;

            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                var info = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashInfo>(),
                    Window = handle,
                    Flags = FlashAll | FlashUntilForeground,

                    // Ноль означает «пока не вернутся»: предел задан флагом выше,
                    // а не числом миганий.
                    Count = uint.MaxValue,
                    TimeoutMs = 0
                };

                _ = FlashWindowEx(ref info);
            }
            catch
            {
                // Не мигнуло — не повод ломать завершение операции.
            }
        }
    }
}
