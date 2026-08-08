using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

            // Перехват до всего остального: если что-то упадёт уже на запуске, человек
            // должен увидеть объяснение, а мы — строку в журнале. Раньше любое
            // необработанное исключение означало тихое исчезновение окна.
            HookUnhandledExceptions();

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

        private void HookUnhandledExceptions()
        {
            // Исключение в потоке интерфейса — единственный случай, когда работу можно
            // продолжить: показываем окно и помечаем обработанным, чтобы программа
            // не закрылась посреди операции.
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // Эти два перехвата — только для журнала. Остановить закрытие программы они
            // не могут, но без них причина падения не сохранялась нигде.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogUnhandled(args.ExceptionObject as Exception, "AppDomain");

            // Исключение в задаче, результат которой никто не дождался. Помечаем
            // наблюдённым: само по себе оно программу валить не должно.
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogUnhandled(args.Exception, "Task");
                args.SetObserved();
            };
        }

        /// <summary>
        /// Окно с ошибкой показывается один раз за запуск. Раньше оно ставилось в очередь
        /// на каждое исключение — и когда исключения пошли потоком, очередь из окон стала
        /// частью того же круга: каждое падало с той же ошибкой, никто его не дожидался,
        /// и это возвращалось сюда через UnobservedTaskException.
        /// </summary>
        private bool _alreadyToldTheUser;

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogUnhandled(e.Exception, "Dispatcher");
            e.Handled = true;

            if (_alreadyToldTheUser) return;
            _alreadyToldTheUser = true;

            string message = Strings.Format("Common_UnexpectedErrorMessage", e.Exception.Message);

            // Через диспетчер, а не напрямую: показывать модальное окно изнутри
            // обработчика исключения — верный способ получить второе исключение.
            // И с перехватом внутри: если и окно не покажется, это не должно вернуться
            // сюда же ещё одним необработанным исключением.
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await DialogService.ShowErrorAsync(Strings.Get("Common_UnexpectedErrorTitle"), message);
                }
                catch (Exception ex)
                {
                    LogUnhandled(ex, "ErrorDialog");
                }
            });
        }

        private static void LogUnhandled(Exception? ex, string source)
        {
            if (ex == null) return;

            // Только в файл, не в журнал на экране: журнал — это элемент интерфейса,
            // и запись в него сама способна бросить исключение. Именно так сообщение
            // об ошибке однажды стало причиной следующей ошибки (см. LogToFileOnly).
            //
            // Тип и место записываем тоже: одного текста сообщения обычно не хватает,
            // чтобы понять, откуда оно.
            AppLogger.LogToFileOnly(Strings.Format("Common_UnexpectedErrorLog", source, ex.GetType().Name, ex.Message));
        }
    }
}
