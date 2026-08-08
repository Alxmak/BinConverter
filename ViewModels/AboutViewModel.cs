using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
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

        /// <summary>«Версия 2.7.0» — строка собирается кодом, разметка её не обновит.
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

        // Своего сервера у программы нет: письмо составляется здесь, а отправляет его
        // почтовый клиент пользователя. Ни хостинга, ни домена, ни ключей для этого
        // не нужно — и никакие введённые данные никуда не уходят без его участия.

        /// <summary>Адрес показывается в карточке, чтобы написать можно было и вручную.</summary>
        public string ContactEmail => FeedbackLetter.ContactEmail;

        /// <summary>Ссылка под этим адресом: нажатие открывает пустое письмо в почтовом
        /// клиенте. Отдельно от <see cref="ContactEmail"/>, потому что показывать в тексте
        /// «mailto:» незачем.</summary>
        public string ContactMailtoUri => "mailto:" + FeedbackLetter.ContactEmail;

        [ObservableProperty] private string feedbackName = "";
        [ObservableProperty] private string feedbackReplyTo = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSendFeedback))]
        [NotifyCanExecuteChangedFor(nameof(SendFeedbackCommand))]
        [NotifyCanExecuteChangedFor(nameof(CopyFeedbackCommand))]
        private string feedbackMessage = "";

        [ObservableProperty] private string feedbackStatusText = "";

        /// <summary>Пустое письмо отправлять незачем — имя и обратный адрес необязательны.</summary>
        public bool CanSendFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

        [RelayCommand(CanExecute = nameof(CanSendFeedback))]
        private async System.Threading.Tasks.Task SendFeedbackAsync()
        {
            string subject = Strings.Format("Feedback_MailSubject", UpdateService.GetCurrentVersion());
            string body = BuildFeedbackBody();

            string uri = FeedbackLetter.BuildMailtoUri(FeedbackLetter.ContactEmail, subject, body);

            // Длинное письмо в ссылку не влезает и приехало бы обрезанным (см. MaxUriLength),
            // поэтому открываем пустое письмо, а текст отдаём через буфер обмена.
            bool tooLongForLink = !FeedbackLetter.FitsInMailto(uri);
            if (tooLongForLink)
            {
                uri = FeedbackLetter.BuildMailtoUri(FeedbackLetter.ContactEmail, subject, "");
                TryCopyToClipboard(body);
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // Самый частый случай на чистой Windows: почтовый клиент не назначен.
                // Терять уже набранный текст нельзя — отдаём его через буфер обмена
                // и называем адрес, поля при этом не очищаем.
                AppLogger.Log(Strings.Format("Feedback_MailClientFailedLog", ex.Message));

                bool copied = TryCopyToClipboard(body);
                FeedbackStatusText = "";
                await DialogService.ShowWarningAsync(Strings.Get("Feedback_Title"),
                    Strings.Format(copied ? "Feedback_NoMailClientCopied" : "Feedback_NoMailClient", FeedbackLetter.ContactEmail));
                return;
            }

            FeedbackStatusText = tooLongForLink
                ? Strings.Get("Feedback_OpenedPasteHint")
                : Strings.Get("Feedback_OpenedStatus");
        }

        /// <summary>Запасной путь: письмо в буфер обмена, отправить можно откуда угодно.</summary>
        [RelayCommand(CanExecute = nameof(CanSendFeedback))]
        private void CopyFeedback()
        {
            FeedbackStatusText = TryCopyToClipboard(BuildFeedbackBody())
                ? Strings.Get("Feedback_CopiedStatus")
                : Strings.Get("Feedback_CopyFailedStatus");
        }

        private string BuildFeedbackBody() => FeedbackLetter.BuildBody(
            FeedbackName, FeedbackReplyTo, FeedbackMessage,
            UpdateService.GetCurrentVersion(), RuntimeInformation.OSDescription);

        private static bool TryCopyToClipboard(string text)
        {
            // Буфер обмена в Windows — общий ресурс: пока им владеет другое приложение,
            // запись падает с COM-ошибкой. Это не повод ронять программу — сообщаем
            // о неудаче вызывающему коду, и он предлагает написать вручную.
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log(Strings.Format("Common_ClipboardFailedLog", ex.Message));
                return false;
            }
        }
    }
}
