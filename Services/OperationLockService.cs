using CommunityToolkit.Mvvm.ComponentModel;

namespace BinConverter.Services
{
    /// <summary>
    /// Единая на всё приложение точка правды "идёт ли сейчас операция" (разбиение или сборка).
    /// Каждая ViewModel, которая начинает длительную операцию, выставляет IsBusy=true здесь —
    /// оболочка (ShellWindow) подписана на это и блокирует переключение разделов меню,
    /// пока операция не завершится (успешно, с ошибкой или отменой).
    /// </summary>
    public partial class OperationLockService : ObservableObject
    {
        public static OperationLockService Instance { get; } = new();

        [ObservableProperty]
        private bool isBusy;

        public bool IsNotBusy => !IsBusy;

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

        private OperationLockService() { }
    }
}
