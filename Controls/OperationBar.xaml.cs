using System.Windows;
using System.Windows.Controls;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Строка управления операцией: «Начать» / «Пауза» / «Отмена» и статус справа.
    /// Стоит в «Конвертировании» и «Сборке файла»; из всей строки различается только
    /// подпись первой кнопки, поэтому она и вынесена в свойство. Команды и остальные
    /// привязки берутся у ViewModel страницы: StartCommand, TogglePauseCommand,
    /// CancelCommand, CanPause, IsBusy, PauseButtonText.
    ///
    /// В «Проверке SHA-256» строка другая (две кнопки, без паузы и без статуса) —
    /// подгонять её под общий контрол значило бы добавить больше параметров,
    /// чем экономится разметки.
    /// </summary>
    public partial class OperationBar : UserControl
    {
        public static readonly DependencyProperty StartTextProperty =
            DependencyProperty.Register(nameof(StartText), typeof(string), typeof(OperationBar),
                new PropertyMetadata(""));

        public string StartText
        {
            get => (string)GetValue(StartTextProperty);
            set => SetValue(StartTextProperty, value);
        }

        public OperationBar()
        {
            InitializeComponent();
        }
    }
}
