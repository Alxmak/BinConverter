using System.Windows;
using System.Windows.Controls;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Плашка-подсказка в карточке «Общая информация»: значок и строка о том, что
    /// нужно сделать, чтобы данные появились. Стоит на всех вкладках, где такая
    /// карточка есть, поэтому вынесена в контрол: текст у вкладок разный (файл,
    /// цепочка частей, дамп), а вид должен быть один.
    /// </summary>
    public partial class HintBanner : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(HintBanner),
                new PropertyMetadata(""));

        /// <summary>Уточнение под основной строкой. Есть не у всех вкладок: в «Сборке
        /// файла» это правило именования частей, в остальных подсказка одна.</summary>
        public static readonly DependencyProperty NoteProperty =
            DependencyProperty.Register(nameof(Note), typeof(string), typeof(HintBanner),
                new PropertyMetadata(""));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public string Note
        {
            get => (string)GetValue(NoteProperty);
            set => SetValue(NoteProperty, value);
        }

        public HintBanner()
        {
            InitializeComponent();
        }
    }
}
