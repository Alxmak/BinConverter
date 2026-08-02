using System.Windows;
using TweakFirmware.Core;
using TweakFirmware.Services;
using TweakFirmware.Views;

namespace TweakFirmware
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Тема применяется ДО создания окна — иначе будет заметное мигание
            // "сначала светлая тема, потом тёмная" при запуске.
            ThemeService.Initialize();

            // Журнал подписывается на события ДО первой записи — иначе "Приложение запущено"
            // не попадёт в общую (единственную на всё приложение, не теряющуюся между разделами) историю.
            LogService.EnsureInitialized();
            AppLogger.Log("Приложение запущено");

            var window = new ShellWindow();
            window.Show();
        }
    }
}
