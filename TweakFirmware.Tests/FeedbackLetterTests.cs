using System;
using System.Linq;
using TweakFirmware.Core;
using Xunit;

namespace TweakFirmware.Tests
{
    /// <summary>
    /// Письмо обратной связи уходит через почтовый клиент, поэтому проверить его руками
    /// значит каждый раз открывать почту и разглядывать, что доехало. Ошибки здесь тихие:
    /// неверное кодирование обрезает письмо на первом особом символе, а слишком длинная
    /// ссылка теряет хвост уже в самом клиенте.
    ///
    /// Подписи служебных строк локализованы, поэтому тесты проверяют структуру и
    /// подставленные значения, а не конкретные русские слова.
    /// </summary>
    public class FeedbackLetterTests
    {
        private const string Version = "2.5.2";
        private const string Os = "Microsoft Windows NT 10.0.19045.0";

        // ============ Текст письма ============

        [Fact]
        public void BuildBody_KeepsMessageTextAtTheTop()
        {
            string body = FeedbackLetter.BuildBody("Alxmak", "a@b.c", "Не открывается лог", Version, Os);

            Assert.StartsWith("Не открывается лог", body);
        }

        [Fact]
        public void BuildBody_IncludesEveryFilledField()
        {
            string body = FeedbackLetter.BuildBody("Alxmak", "a@b.c", "текст", Version, Os);

            Assert.Contains("Alxmak", body);
            Assert.Contains("a@b.c", body);
            Assert.Contains(Version, body);
            Assert.Contains(Os, body);
        }

        [Fact]
        public void BuildBody_BlankOptionalFields_ProduceNoEmptyLines()
        {
            // Имя и обратный адрес необязательны: без них в письме должны остаться
            // только две служебные строки (версия и система), без пустых подписей.
            string body = FeedbackLetter.BuildBody(null, "   ", "текст", Version, Os);

            Assert.Equal(2, FooterLineCount(body));
            Assert.DoesNotContain(": \r\n", body);
            Assert.DoesNotContain("Alxmak", body);
        }

        [Fact]
        public void BuildBody_AllFieldsFilled_HasFourFooterLines()
        {
            string body = FeedbackLetter.BuildBody("Alxmak", "a@b.c", "текст", Version, Os);

            Assert.Equal(4, FooterLineCount(body));
        }

        [Fact]
        public void BuildBody_NothingToReport_ReturnsMessageOnly()
        {
            // Крайний случай: ни версии, ни системы, ни контактов — служебного блока быть
            // не должно, и уж точно не отдельно висящий разделитель.
            string body = FeedbackLetter.BuildBody(null, null, "просто текст", null, null);

            Assert.Equal("просто текст", body);
        }

        [Fact]
        public void BuildBody_EmptyMessage_StillReportsVersionWithoutLeadingBlankLines()
        {
            string body = FeedbackLetter.BuildBody(null, null, "", Version, Os);

            Assert.StartsWith("---", body);
            Assert.Contains(Version, body);
        }

        [Fact]
        public void BuildBody_TrimsMessageAndFields()
        {
            string body = FeedbackLetter.BuildBody("  Alxmak  ", "  a@b.c  ", "\r\n  текст  \r\n", Version, Os);

            Assert.StartsWith("текст\r\n", body);
            Assert.Contains("Alxmak\r\n", body);
            Assert.DoesNotContain("  Alxmak", body);
        }

        [Fact]
        public void BuildBody_NormalizesLineBreaksToCrLf()
        {
            // Вставка из буфера может принести одиночные \n — почтовые клиенты Windows
            // ждут CRLF, иначе строки склеиваются в одну.
            string body = FeedbackLetter.BuildBody(null, null, "первая\nвторая\rтретья", Version, Os);

            Assert.Contains("первая\r\nвторая\r\nтретья", body);
            // Одиночных \n не осталось: после вычёркивания всех CRLF переводов строк нет вовсе.
            Assert.DoesNotContain("\n", body.Replace("\r\n", ""));
        }

        // ============ Ссылка mailto: ============

        [Fact]
        public void BuildMailtoUri_StartsWithAddressThenSubject()
        {
            string uri = FeedbackLetter.BuildMailtoUri("a@b.c", "тема", "текст");

            Assert.StartsWith("mailto:a@b.c?subject=", uri);
        }

        [Fact]
        public void BuildMailtoUri_EncodesSpacesAndCyrillic()
        {
            string uri = FeedbackLetter.BuildMailtoUri("a@b.c", "две темы", "");

            Assert.Contains("%20", uri);
            Assert.DoesNotContain(" ", uri);
            Assert.Contains("%D0", uri); // кириллица в UTF-8
        }

        [Fact]
        public void BuildMailtoUri_EncodesLineBreaks()
        {
            string uri = FeedbackLetter.BuildMailtoUri("a@b.c", "тема", "первая\r\nвторая");

            Assert.Contains("%0D%0A", uri);
            Assert.DoesNotContain("\r", uri);
            Assert.DoesNotContain("\n", uri);
        }

        [Fact]
        public void BuildMailtoUri_AmpersandInMessageCannotAddQueryParameter()
        {
            // Главное свойство кодирования: & из текста не должен обрывать письмо
            // и превращать остаток в лишний параметр ссылки.
            string uri = FeedbackLetter.BuildMailtoUri("a@b.c", "тема", "A&B=C");

            // Единственный & в ссылке — тот, что отделяет тему от текста.
            Assert.Equal(1, uri.Count(c => c == '&'));
            Assert.Contains("%26", uri);
            Assert.DoesNotContain("A&B", uri);
        }

        [Theory]
        [InlineData("#")]
        [InlineData("?")]
        [InlineData("=")]
        [InlineData("+")]
        public void BuildMailtoUri_EncodesReservedCharacters(string special)
        {
            string uri = FeedbackLetter.BuildMailtoUri("a@b.c", "тема", "до" + special + "после");

            // После "&body=" никаких служебных символов остаться не должно.
            string bodyPart = uri.Substring(uri.IndexOf("&body=", StringComparison.Ordinal) + 6);
            Assert.DoesNotContain(special, bodyPart);
        }

        [Fact]
        public void BuildMailtoUri_EmptyBody_OmitsBodyParameterEntirely()
        {
            string uri = FeedbackLetter.BuildMailtoUri("a@b.c", "тема", "");

            Assert.DoesNotContain("body=", uri);
        }

        // ============ Ограничение длины ============

        [Fact]
        public void FitsInMailto_BoundaryIsInclusive()
        {
            Assert.True(FeedbackLetter.FitsInMailto(new string('x', FeedbackLetter.MaxUriLength)));
            Assert.False(FeedbackLetter.FitsInMailto(new string('x', FeedbackLetter.MaxUriLength + 1)));
        }

        [Fact]
        public void FitsInMailto_ShortLetterFits()
        {
            string body = FeedbackLetter.BuildBody("Alxmak", "a@b.c", "Кнопка отмены не работает.", Version, Os);
            string uri = FeedbackLetter.BuildMailtoUri(FeedbackLetter.ContactEmail, "Tweak Firmware 2.5.2", body);

            Assert.True(FeedbackLetter.FitsInMailto(uri));
        }

        [Fact]
        public void FitsInMailto_LongCyrillicLetterDoesNotFit_WhichIsWhyClipboardFallbackExists()
        {
            // 500 русских букв — это уже около 3000 символов закодированной ссылки.
            string body = FeedbackLetter.BuildBody(null, null, new string('я', 500), Version, Os);
            string uri = FeedbackLetter.BuildMailtoUri(FeedbackLetter.ContactEmail, "тема", body);

            Assert.False(FeedbackLetter.FitsInMailto(uri));
        }

        [Fact]
        public void ContactEmail_LooksLikeAnAddress()
        {
            Assert.Contains("@", FeedbackLetter.ContactEmail);
            Assert.DoesNotContain(" ", FeedbackLetter.ContactEmail);
        }

        // ============ Вспомогательное ============

        /// <summary>Сколько служебных строк идёт после разделителя.</summary>
        private static int FooterLineCount(string body)
        {
            int at = body.IndexOf("---", StringComparison.Ordinal);
            if (at < 0) return 0;
            return body.Substring(at).Split("\r\n").Length - 1;
        }
    }
}
