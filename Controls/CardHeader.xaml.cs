using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// Целиком Wpf.Ui.Controls подключать нельзя: имена TextBlock, Grid и другие
// конфликтуют с System.Windows.Controls (см. CLAUDE.md).
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Строка заголовка карточки: значок в цветной плитке и название рядом.
    ///
    /// Отдельным контролом, а не шестью одинаковыми строками в разметке «Настроек»:
    /// карточек там шесть, и стоит одной разъехаться по отступу между плиткой
    /// и названием, как столбец перестаёт читаться списком.
    /// </summary>
    public partial class CardHeader : UserControl
    {
        public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(CardHeader),
                new PropertyMetadata(SymbolRegular.Info24));

        /// <summary>Цвет значка; плитка под ним красится им же, но приглушённо.</summary>
        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(CardHeader),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(CardHeader),
                new PropertyMetadata(""));

        public SymbolRegular Symbol
        {
            get => (SymbolRegular)GetValue(SymbolProperty);
            set => SetValue(SymbolProperty, value);
        }

        public Brush Accent
        {
            get => (Brush)GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public CardHeader()
        {
            InitializeComponent();
        }
    }
}
