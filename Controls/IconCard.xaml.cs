using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// Целиком Wpf.Ui.Controls подключать нельзя: имена TextBlock, Grid и другие
// конфликтуют с System.Windows.Controls (см. CLAUDE.md).
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Карточка со значком в цветной плитке: название с пояснением, место под действие
    /// справа и под дополнительное содержимое (кнопки, ссылки, итог последнего нажатия)
    /// под пояснением.
    ///
    /// Называлась AboutCard, пока стояла только в «О программе». Теперь из неё же собраны
    /// карточки «Настроек», у которых есть действие, — и имя по вкладке перестало быть
    /// правдой.
    ///
    /// Вынесена в контрол по той же причине, что <see cref="ProgressRow"/>: одинаковых
    /// карточек несколько, а вид у них должен быть один — при копировании разметки первым
    /// же разъезжается отступ у плитки, и список перестаёт читаться.
    /// </summary>
    public partial class IconCard : UserControl
    {
        public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(IconCard),
                new PropertyMetadata(SymbolRegular.Info24));

        /// <summary>Цвет значка; плитка под ним красится им же, но приглушённо.</summary>
        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(IconCard),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(IconCard),
                new PropertyMetadata(""));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(IconCard),
                new PropertyMetadata(""));

        /// <summary>Действие справа от текста — обычно одна кнопка.</summary>
        public static readonly DependencyProperty ActionProperty =
            DependencyProperty.Register(nameof(Action), typeof(object), typeof(IconCard),
                new PropertyMetadata(null));

        /// <summary>Всё, что идёт под пояснением: ссылки, кнопки, строка итога.</summary>
        public static readonly DependencyProperty ExtraProperty =
            DependencyProperty.Register(nameof(Extra), typeof(object), typeof(IconCard),
                new PropertyMetadata(null));

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

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public object? Action
        {
            get => GetValue(ActionProperty);
            set => SetValue(ActionProperty, value);
        }

        public object? Extra
        {
            get => GetValue(ExtraProperty);
            set => SetValue(ExtraProperty, value);
        }

        public IconCard()
        {
            InitializeComponent();
        }
    }
}
