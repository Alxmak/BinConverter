using System.Windows;
using System.Windows.Controls;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Пометка-плашка рядом с заголовком: номер версии в шапке «О программе»
    /// и «Beta» у группы «Работа с разделами» в меню. Вид у обеих один, размер
    /// разный — его задаёт вызывающая сторона через <see cref="Control.FontSize"/>
    /// и <see cref="Control.Padding"/>.
    /// </summary>
    public partial class Badge : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(Badge),
                new PropertyMetadata(""));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public Badge()
        {
            InitializeComponent();
        }
    }
}
