using System.Threading.Tasks;

namespace TweakFirmware.Services
{
    public enum DialogChoice { Primary, Secondary, Close }

    /// <summary>
    /// Все всплывающие уведомления в программе идут через этот сервис — единый
    /// современный вид (Wpf.Ui.Controls.MessageBox, в стиле Fluent) вместо
    /// устаревших системных окон Windows.
    /// </summary>
    public static class DialogService
    {
        public static async Task ShowInfoAsync(string title, string message)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "ОК"
            };
            await box.ShowDialogAsync();
        }

        public static Task ShowWarningAsync(string title, string message) => ShowInfoAsync(title, message);
        public static Task ShowErrorAsync(string title, string message) => ShowInfoAsync(title, message);

        /// <summary>
        /// Диалог с 2-3 вариантами ответа. Primary — основное действие, Secondary — альтернативное,
        /// Close — закрыть/отменить. secondaryText можно не указывать, если вариантов только два.
        /// </summary>
        public static async Task<DialogChoice> ShowConfirmAsync(string title, string message, string primaryText, string? secondaryText, string closeText)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText
            };
            if (secondaryText != null) box.SecondaryButtonText = secondaryText;

            var result = await box.ShowDialogAsync();

            // Сравниваем по имени значения, а не по самому enum — устойчиво к точному
            // набору значений в конкретной версии библиотеки.
            return result.ToString() switch
            {
                "Primary" => DialogChoice.Primary,
                "Secondary" => DialogChoice.Secondary,
                _ => DialogChoice.Close
            };
        }
    }
}
