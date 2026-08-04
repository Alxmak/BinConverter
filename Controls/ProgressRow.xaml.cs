using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Строка прогресса: подпись, скруглённый бар, проценты и необязательная подпись под ним.
    /// Вынесена в отдельный контрол, потому что одна и та же строка встречается
    /// на всех трёх рабочих вкладках — иначе разъезжаются отступы и размеры.
    /// </summary>
    public partial class ProgressRow : UserControl
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(ProgressRow),
                new PropertyMetadata(""));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressRow),
                new PropertyMetadata(0d));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ProgressRow),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED))));

        public static readonly DependencyProperty CaptionProperty =
            DependencyProperty.Register(nameof(Caption), typeof(string), typeof(ProgressRow),
                new PropertyMetadata(""));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public string Caption
        {
            get => (string)GetValue(CaptionProperty);
            set => SetValue(CaptionProperty, value);
        }

        public ProgressRow()
        {
            InitializeComponent();
        }
    }
}
