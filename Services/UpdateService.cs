using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinConverter.Services
{
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string? LatestVersion { get; init; }
        public string? ReleaseUrl { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Сравнивает текущую версию программы с последним релизом на GitHub.
    /// Использует публичный REST API GitHub (без токена — работает для публичных
    /// репозиториев и укладывается в лимит запросов без авторизации).
    /// </summary>
    public static class UpdateService
    {
        // ЗАМЕНИ на свои значения после публикации репозитория на GitHub —
        // например "ivanov" и "BinConverter".
        private const string GitHubOwner = "Alxmak";
        private const string GitHubRepo = "BinConverter";

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub API требует обязательный User-Agent, иначе отвечает 403.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BinConverter", GetCurrentVersion()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        public static async Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            string current = GetCurrentVersion();

            if (GitHubOwner == "YOUR_GITHUB_USERNAME")
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    ErrorMessage = "Адрес репозитория ещё не настроен (см. UpdateService.cs)."
                };
            }

            try
            {
                string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
                using var response = await _http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = current,
                        ErrorMessage = $"GitHub ответил: {(int)response.StatusCode} {response.ReasonPhrase}"
                    };
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                string tagName = root.GetProperty("tag_name").GetString() ?? "";
                string htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                string latest = tagName.TrimStart('v', 'V');

                bool isNewer = TryParseVersion(latest, out var latestVer) &&
                               TryParseVersion(current, out var currentVer) &&
                               latestVer > currentVer;

                return new UpdateCheckResult
                {
                    UpdateAvailable = isNewer,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    ReleaseUrl = htmlUrl
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    ErrorMessage = $"Не удалось проверить обновления: {ex.Message}"
                };
            }
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            // Version.TryParse требует хотя бы Major.Minor — подстрахуемся для "2" или "2.1".
            var parts = text.Split('.');
            string normalized = parts.Length switch
            {
                1 => $"{text}.0.0",
                2 => $"{text}.0",
                _ => text
            };
            return Version.TryParse(normalized, out version!);
        }
    }
}
