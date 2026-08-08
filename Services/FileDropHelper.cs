using System.Windows;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Перетаскивание файлов и папок в поля путей.
    ///
    /// Цель перетаскивания — само поле ввода, а не строка вокруг него. Так и должно быть:
    /// именно на поле указывает подсказка внутри него. Раньше AllowDrop стоял на строке
    /// целиком, и получалось наоборот — файл принимался над подписью и над кнопкой
    /// «Обзор», а над самим полем нет: TextBox обрабатывает перетаскивание сам (он ждёт
    /// текст, а файл отвергает) и до обработчиков строки событие не доходило.
    ///
    /// Отсюда и Preview-события у полей: туннельные версии приходят раньше собственной
    /// обработки TextBox, а отметка Handled её отключает.
    /// </summary>
    public static class FileDropHelper
    {
        /// <summary>
        /// Ответ на DragOver: показать курсор копирования, если тащат файлы и сейчас
        /// это разрешено. Событие помечается обработанным всегда — иначе TextBox
        /// подставит свой курсор «текст сюда нельзя».
        /// </summary>
        public static void SetEffect(DragEventArgs e, bool allowed)
        {
            e.Effects = allowed && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// Пути из брошенного, или <c>null</c>, если бросили не файлы. Событие
        /// помечается обработанным независимо от результата: даже отказ должен
        /// остаться отказом, а не превратиться во вставку текста.
        /// </summary>
        public static string[]? TakePaths(DragEventArgs e, bool allowed)
        {
            e.Handled = true;
            if (!allowed) return null;

            return e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0
                ? paths
                : null;
        }
    }
}
