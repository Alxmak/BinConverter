using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// Целиком Wpf.Ui.Controls подключать нельзя: имена TextBlock, Grid и другие
// конфликтуют с System.Windows.Controls (см. CLAUDE.md).
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Значок в цветной плитке — деталь карточек <see cref="IconCard"/> («О программе»)
    /// и строк <see cref="SettingsRow"/> («Настройки»). Отдельным контролом, потому что
    /// величины внутри связаны между собой: размер плитки, скругление, кегль значка
    /// и прозрачность заливки подобраны друг под друга.
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

        /// <summary>Сторона плитки. Меняется в паре с <see cref="GlyphSize"/>.</summary>
        public static readonly DependencyProperty TileSizeProperty =
            DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(IconTile),
                new PropertyMetadata(46.0));

        /// <summary>Кегль значка внутри плитки.</summary>
        public static readonly DependencyProperty GlyphSizeProperty =
            DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(IconTile),
                new PropertyMetadata(22.0));

        public SymbolRegular Symbol
        {
            get => (SymbolRegular)GetValue(SymbolProperty);
            set => SetValue(SymbolProperty, value);
        }

        public double TileSize
        {
            get => (double)GetValue(TileSizeProperty);
            set => SetValue(TileSizeProperty, value);
        }

        public double GlyphSize
        {
            get => (double)GetValue(GlyphSizeProperty);
            set => SetValue(GlyphSizeProperty, value);
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
