using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Core Azure REST API helper. Wraps HttpClient with bearer-token auth,
    /// 429-throttle retry with exponential backoff, and timeout support.
    /// Equivalent to Invoke-AzRestMethodWithRetry in PowerShell.
    /// </summary>
    public class AzureRestService
    {
        private readonly HttpClient _http;
        private readonly TokenCredential _credential;
        private readonly string _baseUrl;
        private readonly string _tokenScope;

        private AccessToken _cachedToken;
        private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

        public Action<string>? StatusCallback { get; set; }

        public AzureRestService(TokenCredential credential, string environment = "AzureCloud")
        {
            _credential = credential;

            if (environment == "AzureUSGovernment")
            {
                _baseUrl = "https://management.usgovcloudapi.net";
                _tokenScope = "https://management.usgovcloudapi.net/.default";
            }
            else
            {
                _baseUrl = "https://management.azure.com";
                _tokenScope = "https://management.azure.com/.default";
            }

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(90)
            };
        }

        private async Task<string> GetTokenAsync(CancellationToken ct)
        {
            if (DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _cachedToken.Token;

            _cachedToken = await _credential.GetTokenAsync(
                new TokenRequestContext(new[] { _tokenScope }), ct);
            _tokenExpiry = _cachedToken.ExpiresOn;
            return _cachedToken.Token;
        }

        private async Task<HttpRequestMessage> BuildRequestAsync(
            HttpMethod method, string path, string? body, CancellationToken ct)
        {
            var token = await GetTokenAsync(ct);
            var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : _baseUrl + path;

            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return req;
        }

        private async Task<JsonDocument?> SendWithRetryAsync(
            HttpMethod method, string path, string? body,
            int maxRetries, CancellationToken ct)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var req = await BuildRequestAsync(method, path, body, ct);
                    using var resp = await _http.SendAsync(req, ct);

                    if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        int retryAfter = 10;
                        if (resp.Headers.RetryAfter?.Delta is TimeSpan delta)
                            retryAfter = Math.Max((int)delta.TotalSeconds, 5);
                        else
                            retryAfter = Math.Min(10 * (int)Math.Pow(2, attempt), 60);

                        StatusCallback?.Invoke($"Rate limited - waiting {retryAfter}s (attempt {attempt + 1}/{maxRetries})...");
                        await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct);
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                        return null;

                    var content = await resp.Content.ReadAsStringAsync(ct);
                    if (string.IsNullOrWhiteSpace(content))
                        return null;

                    return JsonDocument.Parse(content);
                }
                catch (TaskCanceledException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException) when (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
                catch (Exception ex)
                {
                    StatusCallback?.Invoke($"REST error on {method} {path}: {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        public Task<JsonDocument?> GetAsync(string path, CancellationToken ct = default)
            => SendWithRetryAsync(HttpMethod.Get, path, null, 3, ct);

        public Task<JsonDocument?> PostAsync(string path, string jsonBody, CancellationToken ct = default)
            => SendWithRetryAsync(HttpMethod.Post, path, jsonBody, 3, ct);

        public Task<JsonDocument?> PutAsync(string path, string jsonBody, CancellationToken ct = default)
            => SendWithRetryAsync(HttpMethod.Put, path, jsonBody, 3, ct);

        public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
        {
            try
            {
                using var req = await BuildRequestAsync(HttpMethod.Delete, path, null, ct);
                using var resp = await _http.SendAsync(req, ct);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<JsonDocument?> PatchAsync(string path, string jsonBody, CancellationToken ct = default)
        {
            try
            {
                using var req = await BuildRequestAsync(HttpMethod.Patch, path, jsonBody, ct);
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var content = await resp.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content);
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"PATCH error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Returns raw bearer token for direct HttpClient calls (metrics API, etc.)</summary>
        public async Task<string> GetBearerTokenAsync(CancellationToken ct = default)
            => await GetTokenAsync(ct);

        public string BaseUrl => _baseUrl;
    }
}
