using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Короткая подсказка под полем: «так вводить нельзя». Появляется на отклонённый
    /// ввод и сама пропадает.
    ///
    /// Раньше это жило прямо в code-behind «Конвертирования» и существовало ровно для
    /// одного поля — лимита размера. Вынесено, чтобы тем же способом можно было
    /// отвечать на неверный ввод в любом поле, а не заводить второй такой же попап.
    ///
    /// Цвета берутся из темы: прежний вариант был с зашитым тёмным фоном и оставался
    /// тёмным на светлой теме, тогда как всё остальное в программе следит за темой.
    /// </summary>
    public sealed class InputHintPopup
    {
        private static readonly TimeSpan VisibleFor = TimeSpan.FromMilliseconds(1300);

        private readonly Popup _popup;
        private readonly TextBlock _text;
        private readonly DispatcherTimer _timer;

        public InputHintPopup()
        {
            _text = new TextBlock { FontSize = 12 };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(1),
                Child = _text
            };
            // SetResourceReference — то же, что DynamicResource в разметке: кисть
            // меняется вместе с темой, а не запоминается один раз при создании.
            border.SetResourceReference(Border.BackgroundProperty, "ControlSolidFillColorDefaultBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

            _popup = new Popup
            {
                Child = border,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true,
                // Сообщение живёт по таймеру: закрытие по потере фокуса прятало бы его
                // сразу, ведь ввод продолжается в том же поле.
                StaysOpen = true,
                VerticalOffset = 4
            };

            _timer = new DispatcherTimer { Interval = VisibleFor };
            _timer.Tick += (_, _) => Hide();
        }

        /// <summary>Показывает подсказку под указанным элементом.</summary>
        public void Show(UIElement anchor, string message)
        {
            _text.Text = message;
            _popup.PlacementTarget = anchor;
            _popup.IsOpen = true;

            // Перезапуск, а не продление: отсчёт должен идти от последнего сообщения.
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>
        /// Убрать подсказку немедленно. Нужно при уходе со страницы: попап живёт в своём
        /// окне и на закрытой странице остался бы висеть поверх новой.
        /// </summary>
        public void Hide()
        {
            _timer.Stop();
            _popup.IsOpen = false;
        }
    }
}
