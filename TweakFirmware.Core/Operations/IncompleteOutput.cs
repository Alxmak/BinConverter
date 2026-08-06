using System;
using System.Collections.Generic;
using System.IO;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core.Operations
{
    /// <summary>
    /// Уборка за прерванной операцией. Прерванная нарезка или сборка оставляет на диске
    /// файлы, дописанные не до конца: они выглядят настоящими, но данные в них неполные,
    /// и прошить такой файл — худшее, что может случиться с этой программой. Поэтому
    /// после отмены и после ошибки они удаляются.
    /// </summary>
    public static class IncompleteOutput
    {
        /// <summary>
        /// Удаляет перечисленные файлы, сообщая о каждом в журнал.
        /// Возвращает число фактически удалённых — оно может быть меньше переданного,
        /// если файл не успел появиться или его удерживает другая программа.
        /// </summary>
        public static int Remove(IReadOnlyList<string> createdFiles, Action<string> log)
        {
            if (createdFiles.Count == 0) return 0;

            log(Strings.Format("Common_DeletingIncompleteLog", createdFiles.Count));

            int removed = 0;
            foreach (string path in createdFiles)
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    File.Delete(path);
                    removed++;
                    log(Strings.Format("Common_DeletedLog", Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    // Не бросаем дальше: уборка идёт в обработчике другой ошибки, и потерять
                    // из-за неё исходную причину было бы хуже, чем оставить файл на диске.
                    log(Strings.Format("Common_DeleteFailedLog", Path.GetFileName(path), ex.Message));
                }
            }

            return removed;
        }

        /// <summary>
        /// Кончилось ли место на диске. Отличать этот случай важно: пользователю нужно
        /// сказать «освободите место», а не показывать код ошибки Windows.
        /// </summary>
        public static bool IsDiskFull(Exception ex)
        {
            // ERROR_HANDLE_DISK_FULL (0x27) и ERROR_DISK_FULL (0x70) — младшее слово HRESULT.
            int code = ex.HResult & 0xFFFF;
            return ex is IOException && (code == 0x27 || code == 0x70);
        }
    }
}
