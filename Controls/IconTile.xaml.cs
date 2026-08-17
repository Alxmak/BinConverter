using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// Целиком Wpf.Ui.Controls подключать нельзя: имена TextBlock, Grid и другие
// конфликтуют с System.Windows.Controls (см. CLAUDE.md).
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Значок в цветной плитке — деталь карточки <see cref="IconCard"/>. Отдельным
    /// контролом, потому что величины внутри связаны между собой: размер плитки,
    /// скругление, кегль значка и прозрачность заливки подобраны друг под друга.
    /// </summary>
    public partial class IconTile : UserControl
    {
        public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(IconTile),
                new PropertyMetadata(SymbolRegular.Info24));

        /// <summary>Цвет значка; плитка под ним красится им же, но приглушённо.</summary>
        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(IconTile),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))));

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

        public IconTile()
        {
            InitializeComponent();
        }
    }
}
