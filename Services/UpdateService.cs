using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TweakFirmware.Core;
using TweakFirmware.Core.Localization;

namespace TweakFirmware.Services
{
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string? LatestVersion { get; init; }
        public string? ReleaseUrl { get; init; }
        public string? ErrorMessage { get; init; }

        // Пункт: для автообновления нужна прямая ссылка на .exe-установщик из ассетов
        // релиза, а не просто страница релиза (её пользователь не должен открывать сам).
        public string? InstallerAssetUrl { get; init; }
        public string? InstallerAssetName { get; init; }
    }

    /// <summary>
    /// Сравнивает текущую версию программы с последним релизом на GitHub, и умеет
    /// скачать сам файл установщика (используется автообновлением — см. UpdateManager).
    /// Использует публичный REST API GitHub (без токена — работает для публичных
    /// репозиториев и укладывается в лимит запросов без авторизации).
    /// </summary>
    public static class UpdateService
    {
        // Имя репозитория на GitHub — не переименовывается вместе с программой,
        // иначе проверка обновлений будет ходить по несуществующему адресу.
        private const string GitHubOwner = "Alxmak";
        private const string GitHubRepo = "BinConverter";

        /// <summary>
        /// Страница программы на GitHub — её открывает кнопка во вкладке «О программе».
        /// Собрана из тех же owner и repo, что и запрос к API: вписанный рядом второй
        /// адрес того же репозитория однажды разъехался бы с первым.
        /// </summary>
        public const string RepositoryUrl = "https://github.com/" + GitHubOwner + "/" + GitHubRepo;

        // Имя файла установщика без версии и расширения — должно совпадать
        // с OutputBaseFilename в installer/Setup.iss.
        private const string InstallerAssetPrefix = "TweakFirmwareSetup";

        // Размер буфера при скачивании установщика. В Core такие значения давно вынесены
        // в именованные константы (HashHelper.BufferSize, FileMerger.BufferSize) — здесь
        // 81920 был просто продублирован дважды. Значение то же, что по умолчанию
        // использует Stream.CopyTo, для файла на несколько десятков МБ его достаточно.
        private const int DownloadBufferSize = 81920;

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            // Без ограничения по времени на уровне клиента — скачивание установщика
            // может занять больше, чем разумный таймаут для короткого запроса к API.
            // Быстрый отказ для самого запроса к API обеспечивается отдельным
            // CancellationToken с коротким таймаутом в CheckForUpdateAsync.
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            // GitHub API требует обязательный User-Agent, иначе отвечает 403.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TweakFirmware", GetCurrentVersion()));
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

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
                using var response = await _http.GetAsync(url, timeoutCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = current,
                        ErrorMessage = Strings.Format("Update_GitHubRespondedError", (int)response.StatusCode, response.ReasonPhrase ?? "")
                    };
                }

                using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
                var root = doc.RootElement;

                string tagName = root.GetProperty("tag_name").GetString() ?? "";
                string htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                string latest = tagName.TrimStart('v', 'V');

                bool isNewer = VersionHelper.IsNewer(latest, current);

                string? installerUrl = null;
                string? installerName = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        if (name.StartsWith(InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            installerUrl = asset.TryGetProperty("browser_download_url", out var urlProp2) ? urlProp2.GetString() : null;
                            installerName = name;
                            break;
                        }
                    }
                }

                return new UpdateCheckResult
                {
                    UpdateAvailable = isNewer,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    ReleaseUrl = htmlUrl,
                    InstallerAssetUrl = installerUrl,
                    InstallerAssetName = installerName
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    ErrorMessage = Strings.Format("Update_CheckFailedError", ex.Message)
                };
            }
        }

        /// <summary>Скачивает файл установщика во временную папку и возвращает путь к нему.</summary>
        public static async Task<string> DownloadInstallerAsync(
            string url, string fileName, IProgress<double>? progress, CancellationToken ct)
        {
            // GetFileName обязателен: имя приходит из описания релиза на GitHub, то есть
            // снаружи. Имя вида "..\..\что-то.exe" вывело бы запись за пределы %TEMP%.
            string safeName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "TweakFirmwareSetup.exe";

            string tempPath = Path.Combine(Path.GetTempPath(), safeName);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true);

            byte[] buffer = new byte[DownloadBufferSize];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (totalBytes > 0)
                    progress?.Report((double)totalRead / totalBytes * 100.0);
            }

            return tempPath;
        }
    }
}
