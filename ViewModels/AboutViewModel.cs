using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class AboutViewModel : LocalizedViewModel
    {
        /// <summary>Номер версии для плашки рядом с названием — без слова «Версия»:
        /// в плашке оно и так подразумевается, а места занимает больше самого номера.</summary>
        public string VersionText => UpdateService.GetCurrentVersion();

        /// <summary>А вот всплывающая подсказка к плашке слово «Версия» проговаривает:
        /// иначе непонятно, номер это чего.</summary>
        [ObservableProperty] private string versionTooltip;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCheck))]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        private bool isChecking;

        [ObservableProperty] private string updateStatusText = "";

        public bool CanCheck => !IsChecking;

        public AboutViewModel()
        {
            versionTooltip = BuildVersionTooltip();
        }

        private static string BuildVersionTooltip() =>
            Strings.Format("About_VersionText", UpdateService.GetCurrentVersion());

        /// <summary>Тексты, собранные кодом: подсказка «Версия N» и строки итогов.
        /// Разметка их не обновит — она знает только про ключи, а эти строки собраны
        /// уже готовыми. Сам номер версии в плашке от языка не зависит.</summary>
        protected override void OnLanguageChanged()
        {
            VersionTooltip = BuildVersionTooltip();

            // Итоги проверки обновлений и копирования адреса собраны кодом, поэтому
            // сами не переведутся. Раньше их намеренно не трогали — «это результат
            // конкретного нажатия», — но выглядело это как непереведённая строка:
            // проверил обновления по-русски, переключил язык, и посреди английской
            // вкладки осталось «У вас последняя версия».
            UpdateStatusText = BuildUpdateStatusText();
            FeedbackStatusText = BuildFeedbackStatusText();
        }

        // ===================== Строка под «Обновлениями» =====================

        /// <summary>Что показывать под «Обновлениями». Хранится не готовой строкой,
        /// а тем, из чего она собирается, — иначе при смене языка строка остаётся
        /// на прежнем языке (см. <see cref="OnLanguageChanged"/>).</summary>
        private enum UpdateStatus { None, Checking, UpToDate, Available }

        private UpdateStatus _updateStatus = UpdateStatus.None;
        private string _latestVersion = "";

        private void SetUpdateStatus(UpdateStatus status, string latestVersion = "")
        {
            _updateStatus = status;
            _latestVersion = latestVersion;
            UpdateStatusText = BuildUpdateStatusText();
        }

        private string BuildUpdateStatusText() => _updateStatus switch
        {
            UpdateStatus.Checking => Strings.Get("About_Checking"),
            UpdateStatus.UpToDate => Strings.Get("About_UpToDate"),
            UpdateStatus.Available => Strings.Format("About_UpdateAvailable", _latestVersion),
            _ => ""
        };

        // Пункт 5: та же логика, что и фоновая ежедневная проверка — если обновление
        // найдено, дальше им занимается общий баннер в ShellWindow (скачивание и тихая
        // установка), а не диалог с переходом в браузер, как было раньше.
        [RelayCommand(CanExecute = nameof(CanCheck))]
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            IsChecking = true;
            SetUpdateStatus(UpdateStatus.Checking);

            var result = await UpdateManager.Instance.CheckNowAsync();

            IsChecking = false;

            if (result == null) return;

            if (result.ErrorMessage != null)
            {
                // Сообщение об ошибке приходит из UpdateService уже собранным и в окне
                // же и остаётся: под карточкой в этом случае не показываем ничего.
                SetUpdateStatus(UpdateStatus.None);
                await DialogService.ShowWarningAsync(Strings.Get("About_CheckUpdatesTitle"), result.ErrorMessage);
                return;
            }

            if (result.UpdateAvailable) SetUpdateStatus(UpdateStatus.Available, result.LatestVersion ?? "");
            else SetUpdateStatus(UpdateStatus.UpToDate);
        }

        // ============================= Обратная связь =============================

        // Своего сервера у программы нет: письмо только открывается в почтовом клиенте
        // пользователя, а отправляет его он сам. Ни хостинга, ни домена, ни ключей для
        // этого не нужно — и без его нажатия «Отправить» уже в почте никуда ничего не уходит.

        /// <summary>Сколько кнопка держит подпись «Адрес скопирован», прежде чем
        /// вернуться к обычной. Двух секунд хватает, чтобы заметить ответ на нажатие,
        /// и мало, чтобы принять изменившуюся подпись за постоянную.</summary>
        private static readonly TimeSpan CopiedCaptionDelay = TimeSpan.FromSeconds(2);

        [ObservableProperty] private string feedbackStatusText = "";

        /// <summary>Кнопка «Скопировать адрес» показывает сейчас подтверждение.</summary>
        [ObservableProperty] private bool addressCopied;

        private DispatcherTimer? _copiedResetTimer;

        /// <summary>Как и статус обновлений, хранится не строкой, а причиной: строку
        /// собирает <see cref="BuildFeedbackStatusText"/> — тогда она переводится
        /// вместе с остальным интерфейсом.</summary>
        private bool _clipboardFailed;

        private string BuildFeedbackStatusText() =>
            _clipboardFailed ? Strings.Get("Feedback_CopyFailedStatus") : "";

        /// <summary>
        /// Открывает письмо в почтовом клиенте. Формы из трёх полей здесь больше нет:
        /// набранное в ней всё равно уезжало в то же письмо, которое человек дописывал
        /// и отправлял уже у себя в почте, — то есть форма была почтовым клиентом
        /// внутри почтового клиента. Служебные строки (версия и система) программа
        /// подставляет сама: без них разбор любого сообщения об ошибке начинается
        /// с расспросов.
        /// </summary>
        [RelayCommand]
        private async System.Threading.Tasks.Task WriteFeedbackAsync()
        {
            string subject = Strings.Format("Feedback_MailSubject", UpdateService.GetCurrentVersion());
            string body = FeedbackLetter.BuildBody(null, null, "",
                UpdateService.GetCurrentVersion(), RuntimeInformation.OSDescription);

            string uri = FeedbackLetter.BuildMailtoUri(FeedbackLetter.ContactEmail, subject, body);

            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // Самый частый случай на чистой Windows: почтовый клиент не назначен.
                // Тогда единственное, чем можно помочь, — отдать адрес в буфер обмена
                // и назвать его в сообщении, чтобы написать можно было откуда угодно.
                AppLogger.Log(Strings.Format("Feedback_MailClientFailedLog", ex.Message));

                bool copied = ClipboardHelper.TryCopy(FeedbackLetter.ContactEmail);
                FeedbackStatusText = "";
                await DialogService.ShowWarningAsync(Strings.Get("Feedback_Title"),
                    Strings.Format(copied ? "Feedback_NoMailClientCopied" : "Feedback_NoMailClient", FeedbackLetter.ContactEmail));
                return;
            }

            // Об удачном открытии письма карточка молчит: почтовый клиент показывается
            // сам, а строка «Письмо открыто…» после этого висела бы в карточке до конца
            // работы программы, уже ничего не сообщая.
        }

        /// <summary>
        /// Запасной путь для тех, у кого почта в браузере: адрес в буфер обмена.
        /// Об удаче говорит сама кнопка — её подпись ненадолго меняется. Отдельной
        /// строки под карточкой для этого не нужно: она появлялась навсегда и после
        /// одного нажатия оставалась висеть до конца работы программы.
        /// </summary>
        [RelayCommand]
        private void CopyEmail()
        {
            if (!ClipboardHelper.TryCopy(FeedbackLetter.ContactEmail))
            {
                // Про занятый буфер обмена кнопкой не скажешь: там нужно объяснение
                // и предложение повторить, поэтому остаётся строка под карточкой.
                AddressCopied = false;
                _clipboardFailed = true;
                FeedbackStatusText = BuildFeedbackStatusText();
                return;
            }

            _clipboardFailed = false;
            FeedbackStatusText = "";
            AddressCopied = true;

            // Таймер один на все нажатия: повторное нажатие продлевает подпись, а не
            // заводит второй возврат, который снял бы её раньше времени.
            _copiedResetTimer ??= CreateCopiedResetTimer();
            _copiedResetTimer.Stop();
            _copiedResetTimer.Start();
        }

        private DispatcherTimer CreateCopiedResetTimer()
        {
            var timer = new DispatcherTimer { Interval = CopiedCaptionDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                AddressCopied = false;
            };
            return timer;
        }
    }
}
