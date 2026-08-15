using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;
using TweakFirmware.Services;

namespace TweakFirmware.ViewModels
{
    public partial class AboutViewModel : LocalizedViewModel
    {
        [ObservableProperty] private string versionText;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCheck))]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        private bool isChecking;

        [ObservableProperty] private string updateStatusText = "";

        public bool CanCheck => !IsChecking;

        public AboutViewModel()
        {
            versionText = BuildVersionText();
        }

        private static string BuildVersionText() =>
            Strings.Format("About_VersionText", UpdateService.GetCurrentVersion());

        /// <summary>Строка «Версия N» собирается кодом, разметка её не обновит.
        /// Итоги проверки обновлений и отправки письма не трогаем: это результат
        /// конкретного нажатия, он относится к моменту, когда его показали.</summary>
        protected override void OnLanguageChanged() => VersionText = BuildVersionText();

        // Пункт 5: та же логика, что и фоновая ежедневная проверка — если обновление
        // найдено, дальше им занимается общий баннер в ShellWindow (скачивание и тихая
        // установка), а не диалог с переходом в браузер, как было раньше.
        [RelayCommand(CanExecute = nameof(CanCheck))]
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            IsChecking = true;
            UpdateStatusText = Strings.Get("About_Checking");

            var result = await UpdateManager.Instance.CheckNowAsync();

            IsChecking = false;

            if (result == null) return;

            if (result.ErrorMessage != null)
            {
                UpdateStatusText = "";
                await DialogService.ShowWarningAsync(Strings.Get("About_CheckUpdatesTitle"), result.ErrorMessage);
                return;
            }

            UpdateStatusText = result.UpdateAvailable
                ? Strings.Format("About_UpdateAvailable", result.LatestVersion ?? "")
                : Strings.Get("About_UpToDate");
        }

        // ============================= Обратная связь =============================

        // Своего сервера у программы нет: письмо только открывается в почтовом клиенте
        // пользователя, а отправляет его он сам. Ни хостинга, ни домена, ни ключей для
        // этого не нужно — и без его нажатия «Отправить» уже в почте никуда ничего не уходит.

        /// <summary>Адрес показывается в карточке, чтобы написать можно было и вручную.</summary>
        public string ContactEmail => FeedbackLetter.ContactEmail;

        /// <summary>Ссылка под этим адресом: нажатие открывает пустое письмо в почтовом
        /// клиенте. Отдельно от <see cref="ContactEmail"/>, потому что показывать в тексте
        /// «mailto:» незачем.</summary>
        public string ContactMailtoUri => "mailto:" + FeedbackLetter.ContactEmail;

        [ObservableProperty] private string feedbackStatusText = "";

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

            FeedbackStatusText = Strings.Get("Feedback_OpenedStatus");
        }

        /// <summary>Запасной путь для тех, у кого почта в браузере: адрес в буфер обмена.</summary>
        [RelayCommand]
        private void CopyEmail()
        {
            FeedbackStatusText = ClipboardHelper.TryCopy(FeedbackLetter.ContactEmail)
                ? Strings.Format("Feedback_AddressCopiedStatus", FeedbackLetter.ContactEmail)
                : Strings.Get("Feedback_CopyFailedStatus");
        }
    }
}
