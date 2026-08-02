using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace TweakFirmware.Services
{
    /// <summary>
    /// Сохраняет размер, положение и состояние (обычное/развёрнутое) главного окна между
    /// запусками программы — простой текстовый файл, аналогично ThemeService.
    /// </summary>
    public static class WindowPlacementService
    {
        private static readonly string SettingsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tweak Firmware", "window.txt");

        public static void Restore(Window window)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;

                string[] parts = File.ReadAllText(SettingsPath).Split(';');
                if (parts.Length != 5) return;

                if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out double left)) return;
                if (!double.TryParse(parts[1], CultureInfo.InvariantCulture, out double top)) return;
                if (!double.TryParse(parts[2], CultureInfo.InvariantCulture, out double width)) return;
                if (!double.TryParse(parts[3], CultureInfo.InvariantCulture, out double height)) return;
                if (!Enum.TryParse(parts[4], out WindowState state)) return;

                if (width < window.MinWidth || height < window.MinHeight) return;

                window.Width = width;
                window.Height = height;

                // Пункт 2: если сохранённые координаты были на мониторе, который сейчас
                // отключён (или разрешение/расстановка мониторов изменилась), окно может
                // оказаться полностью за пределами видимой области — тогда оставляем
                // WindowStartupLocation по умолчанию (окно центрируется на текущем экране),
                // но размер всё равно восстанавливаем.
                if (FitsOnCurrentScreens(left, top, width, height))
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = left;
                    window.Top = top;
                }

                window.WindowState = state == WindowState.Minimized ? WindowState.Normal : state;
            }
            catch { /* используем размеры и положение по умолчанию, если не удалось прочитать */ }
        }

        /// <summary>
        /// Хотя бы небольшая часть окна (достаточная, чтобы за неё можно было ухватиться
        /// мышью и перетащить обратно) должна попадать в объединённую область всех сейчас
        /// подключённых мониторов.
        /// </summary>
        private static bool FitsOnCurrentScreens(double left, double top, double width, double height)
        {
            const double MinVisible = 80;

            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vRight = vLeft + SystemParameters.VirtualScreenWidth;
            double vBottom = vTop + SystemParameters.VirtualScreenHeight;

            double visibleWidth = Math.Min(left + width, vRight) - Math.Max(left, vLeft);
            double visibleHeight = Math.Min(top + height, vBottom) - Math.Max(top, vTop);

            return visibleWidth >= MinVisible && visibleHeight >= MinVisible;
        }

        public static void Save(Window window)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Для развёрнутого/свёрнутого окна сохраняем размеры обычного (восстановленного)
                // состояния — иначе при следующем запуске окно "прыгнет" на весь экран навсегда.
                Rect bounds = window.WindowState == WindowState.Normal
                    ? new Rect(window.Left, window.Top, window.Width, window.Height)
                    : window.RestoreBounds;

                string line = string.Join(";",
                    bounds.Left.ToString(CultureInfo.InvariantCulture),
                    bounds.Top.ToString(CultureInfo.InvariantCulture),
                    bounds.Width.ToString(CultureInfo.InvariantCulture),
                    bounds.Height.ToString(CultureInfo.InvariantCulture),
                    window.WindowState.ToString());

                File.WriteAllText(SettingsPath, line);
            }
            catch { /* сохранение положения окна не критично для работы программы */ }
        }
    }
}
