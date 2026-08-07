using System.Windows.Controls;

namespace TweakFirmware.Controls
{
    /// <summary>
    /// Баннер итога операции, одинаковый в «Конвертировании», «Сборке файла» и
    /// «Проверке SHA-256». Раньше итог был виден только в модальном окне, которое
    /// закрывалось и не оставляло на экране следа, — а в «Проверке» баннер был,
    /// и разделы выглядели по-разному.
    /// </summary>
    public partial class ResultBanner : UserControl
    {
        public ResultBanner()
        {
            InitializeComponent();
        }
    }
}
