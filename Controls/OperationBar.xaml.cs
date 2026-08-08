using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Строка управления операцией: «Начать» / «Пауза» / «Отмена». Стоит на всех трёх
    /// рабочих вкладках — раньше «Проверка SHA-256» повторяла эти же кнопки своей
    /// разметкой, из-за чего они могли разъехаться по виду.
    ///
    /// Команды передаются страницей, а не разыскиваются по именам свойств в DataContext:
    /// имена у вкладок разные («Сравнить» вместо «Начать»), а неявная договорённость
    /// «у DataContext обязательно есть StartCommand» ломается молча — привязка просто
    /// не находит свойство, и кнопка ничего не делает.
    ///
    /// Кнопка паузы показывается только если страница передала <see cref="PauseCommand"/>:
    /// в сравнении файлов паузы нет.
    /// </summary>
    public partial class OperationBar : UserControl
    {
        public static readonly DependencyProperty StartTextProperty =
            DependencyProperty.Register(nameof(StartText), typeof(string), typeof(OperationBar),
                new PropertyMetadata(""));

        public static readonly DependencyProperty StartCommandProperty =
            DependencyProperty.Register(nameof(StartCommand), typeof(ICommand), typeof(OperationBar),
                new PropertyMetadata(null));

        public static readonly DependencyProperty CancelCommandProperty =
            DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(OperationBar),
                new PropertyMetadata(null));

        public static readonly DependencyProperty PauseCommandProperty =
            DependencyProperty.Register(nameof(PauseCommand), typeof(ICommand), typeof(OperationBar),
                new PropertyMetadata(null, OnPauseCommandChanged));

        /// <summary>Подпись первой кнопки: «Начать» в работе с файлами, «Сравнить» в проверке.</summary>
        public string StartText
        {
            get => (string)GetValue(StartTextProperty);
            set => SetValue(StartTextProperty, value);
        }

        public ICommand? StartCommand
        {
            get => (ICommand?)GetValue(StartCommandProperty);
            set => SetValue(StartCommandProperty, value);
        }

        public ICommand? CancelCommand
        {
            get => (ICommand?)GetValue(CancelCommandProperty);
            set => SetValue(CancelCommandProperty, value);
        }

        /// <summary>Не задана — кнопки паузы не будет.</summary>
        public ICommand? PauseCommand
        {
            get => (ICommand?)GetValue(PauseCommandProperty);
            set => SetValue(PauseCommandProperty, value);
        }

        public OperationBar()
        {
            InitializeComponent();
            UpdatePauseVisibility();
        }

        private static void OnPauseCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((OperationBar)d).UpdatePauseVisibility();

        private void UpdatePauseVisibility() =>
            PauseButton.Visibility = PauseCommand == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
