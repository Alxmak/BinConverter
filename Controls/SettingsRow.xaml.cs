using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// Целиком Wpf.Ui.Controls подключать нельзя: имена TextBlock, Grid и другие
// конфликтуют с System.Windows.Controls (см. CLAUDE.md).
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Строка настройки внутри карточки-группы: значок в плитке, название с пояснением
    /// и действие справа. Своей рамки нет — её рисует карточка группы.
    ///
    /// Отдельно от <see cref="IconCard"/>, хотя устроены они похоже: там каждая карточка
    /// сама по себе, с пояснением на пару строк и высоким действием, поэтому плитка
    /// крупнее и всё выровнено по верху. Здесь строка обычно в одну строку, плитка
    /// меньше, а название стоит по её центру.
    /// </summary>
    public partial class SettingsRow : UserControl
    {
        public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(SettingsRow),
                new PropertyMetadata(SymbolRegular.Info24));

        /// <summary>Цвет значка; плитка под ним красится им же, но приглушённо.</summary>
        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SettingsRow),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsRow),
                new PropertyMetadata(""));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRow),
                new PropertyMetadata(""));

        /// <summary>Действие справа от названия — переключатель или кнопка.</summary>
        public static readonly DependencyProperty ActionProperty =
            DependencyProperty.Register(nameof(Action), typeof(object), typeof(SettingsRow),
                new PropertyMetadata(null));

        /// <summary>
        /// Содержимое под названием — в «Папках» это строка с путём. От него же зависит
        /// выравнивание: со строкой под названием колонка высокая, и плитка встаёт
        /// по верху, а не по центру.
        /// </summary>
        public static readonly DependencyProperty ExtraProperty =
            DependencyProperty.Register(nameof(Extra), typeof(object), typeof(SettingsRow),
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

        public SettingsRow()
        {
            InitializeComponent();
        }
    }
}
