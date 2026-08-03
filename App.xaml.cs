using System.Windows;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;
using TweakFirmware.Views;

namespace TweakFirmware
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Язык — до создания окна, по той же причине, что и тема ниже: чтобы все
            // привязки и статические тексты сразу шли на нужном языке, без "мигания".
            LocalizationService.Instance.Initialize();

            // Тема применяется ДО создания окна — иначе будет заметное мигание
            // "сначала светлая тема, потом тёмная" при запуске.
            ThemeService.Initialize();

            // Журнал подписывается на события ДО первой записи — иначе "Приложение запущено"
            // не попадёт в общую (единственную на всё приложение, не теряющуюся между разделами) историю.
            LogService.EnsureInitialized();
            AppLogger.Log(Strings.Get("Log_AppStarted"));

            var window = new ShellWindow();
            window.Show();

            // Пункт 1/3: проверка обновлений — в фоне, не блокирует запуск, и не чаще
            // одного раза в день (сама проверка частоты — внутри UpdateManager).
            _ = UpdateManager.Instance.CheckOnStartupAsync();
        }
    }
}
