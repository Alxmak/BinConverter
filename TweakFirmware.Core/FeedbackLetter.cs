using System;
using System.Collections.Generic;
using System.Text;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Core
{
    /// <summary>
    /// Сборка письма обратной связи и ссылки mailto: для него.
    ///
    /// Отдельного сервера у программы нет и не нужно: письмо составляет сама программа,
    /// а отправляет его почтовый клиент пользователя. Логика здесь чистая (строки на
    /// входе, строки на выходе), поэтому лежит в Core и покрыта тестами — проверить
    /// вручную кодирование адресной ссылки трудно, а ошибка в нём обрезает письмо
    /// на первом же особом символе.
    /// </summary>
    public static class FeedbackLetter
    {
        /// <summary>Адрес, на который уходят письма.</summary>
        public const string ContactEmail = "italexmak@gmail.com";

        /// <summary>
        /// Практический предел длины mailto:-ссылки. Windows передаёт её почтовому клиенту
        /// через командную строку, и на длинных ссылках письмо приходит обрезанным (у разных
        /// клиентов порог свой, около 2000 символов). Кодирование раздувает текст: одна
        /// русская буква — это шесть символов вида %D0%9F, поэтому даже короткое письмо
        /// подходит к пределу быстро. Если не помещается, вызывающий код отдаёт текст
        /// через буфер обмена, а не молча теряет его хвост.
        /// </summary>
        public const int MaxUriLength = 1800;

        private const string LineBreak = "\r\n";

        /// <summary>Отделяет сообщение пользователя от служебных строк.</summary>
        private const string FooterSeparator = "---";

        /// <summary>
        /// Текст письма: сообщение пользователя, а под ним — как с ним связаться и
        /// на какой версии/системе это писалось. Пустые поля строк не создают.
        /// </summary>
        public static string BuildBody(string? name, string? replyTo, string? message, string? appVersion, string? osDescription)
        {
            var footer = new List<string>();
            AddFooterLine(footer, "Feedback_BodyNameLine", name);
            AddFooterLine(footer, "Feedback_BodyReplyToLine", replyTo);
            AddFooterLine(footer, "Feedback_BodyVersionLine", appVersion);
            AddFooterLine(footer, "Feedback_BodyOsLine", osDescription);

            string text = NormalizeLineBreaks(message).Trim();

            if (footer.Count == 0) return text;

            var sb = new StringBuilder();
            if (text.Length > 0) sb.Append(text).Append(LineBreak).Append(LineBreak);
            sb.Append(FooterSeparator);
            foreach (string line in footer) sb.Append(LineBreak).Append(line);
            return sb.ToString();
        }

        /// <summary>
        /// Ссылка mailto: с темой и текстом. Тему и текст обязательно кодируем:
        /// без этого первый же &amp; в сообщении оборвал бы его и превратил остаток
        /// в лишний параметр ссылки, а перевод строки — сломал бы ссылку целиком.
        /// </summary>
        public static string BuildMailtoUri(string address, string subject, string body)
        {
            var sb = new StringBuilder("mailto:").Append(address);
            sb.Append("?subject=").Append(Uri.EscapeDataString(subject ?? ""));

            // Пустой текст не подставляем вовсе: иначе клиент открывает письмо
            // с пустым телом и не даёт вставить текст поверх (у части клиентов
            // курсор оказывается в конце "пустой" строки).
            if (!string.IsNullOrEmpty(body)) sb.Append("&body=").Append(Uri.EscapeDataString(body));

            return sb.ToString();
        }

        /// <summary>Поместится ли готовая ссылка в ограничение Windows.</summary>
        public static bool FitsInMailto(string? uri) => (uri?.Length ?? 0) <= MaxUriLength;

        private static void AddFooterLine(List<string> lines, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add(Strings.Format(key, value!.Trim()));
        }

        /// <summary>
        /// Приводим переводы строк к CRLF: почтовые клиенты Windows ждут именно их,
        /// а текст может прийти и с одиночными \n (например, вставкой из буфера).
        /// </summary>
        private static string NormalizeLineBreaks(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", LineBreak);
        }
    }
}
