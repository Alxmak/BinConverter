using System;
using System.Runtime.InteropServices;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Не даёт компьютеру уснуть, пока идёт длительная запись.
    ///
    /// Нарезка или сборка многогигабайтного дампа занимает десятки минут, а на внешнем
    /// диске и часы. Всё это время человек за компьютером ничего не делает — ни мыши,
    /// ни клавиатуры, — и обычная политика питания честно усыпляет систему посреди записи.
    /// Просыпается она уже с недописанными файлами, а уборка за прерванной операцией
    /// при этом не срабатывает: программу никто не отменял, её просто остановили.
    ///
    /// Экран при этом гасить не мешаем: смотреть на полосу прогресса всё равно некому,
    /// а горящий экран на ноутбуке — это заметный расход батареи на ровном месте.
    /// </summary>
    internal static class SleepBlocker
    {
        [Flags]
        private enum ExecutionState : uint
        {
            /// <summary>Запомнить состояние до следующего вызова, а не разово продлить.</summary>
            Continuous = 0x80000000,

            /// <summary>Система не должна засыпать. Про экран здесь ничего не сказано.</summary>
            SystemRequired = 0x00000001
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

        /// <summary>Держать систему бодрствующей до <see cref="AllowSleep"/>.</summary>
        public static void KeepAwake() => Apply(ExecutionState.Continuous | ExecutionState.SystemRequired);

        /// <summary>Вернуть обычную политику питания.</summary>
        public static void AllowSleep() => Apply(ExecutionState.Continuous);

        /// <summary>
        /// Неудача здесь не должна ничего ломать: запрет сна — удобство, а не условие
        /// работы. В худшем случае система уснёт так же, как и раньше.
        ///
        /// Состояние запоминается за потоком, который вызвал функцию, поэтому звать её
        /// нужно из потока интерфейса — он живёт всё время работы программы. Рабочий
        /// поток операции после её конца исчезнет вместе с запретом, и снять его будет
        /// уже некому.
        /// </summary>
        private static void Apply(ExecutionState flags)
        {
            try
            {
                _ = SetThreadExecutionState(flags);
            }
            catch
            {
                // Функции может не оказаться на месте только в очень странной системе.
            }
        }
    }
}
