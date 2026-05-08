using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Wraps Azure Resource Graph queries. Handles pagination (skipToken) and
    /// 429 throttling. Equivalent to Search-AzGraphSafe in PowerShell.
    /// </summary>
    public class ResourceGraphService
    {
        private readonly TokenCredential _credential;
        private ArmClient? _armClient;
        public Action<string>? StatusCallback { get; set; }

        public ResourceGraphService(TokenCredential credential)
        {
            _credential = credential;
        }

        private ArmClient GetClient()
        {
            _armClient ??= new ArmClient(_credential);
            return _armClient;
        }

        public async Task<List<JsonElement>> QueryAsync(
            string kqlQuery,
            IEnumerable<string> subscriptionIds,
            int maxResults = 1000,
            CancellationToken ct = default)
        {
            var results = new List<JsonElement>();
            string? skipToken = null;

            var subList = new List<string>(subscriptionIds);
            if (subList.Count == 0)
                return results;

            const int maxRetries = 3;

            var tenant = GetClient().GetTenants().GetAllAsync(cancellationToken: ct);

            // Get the tenant resource to call GetResourcesAsync
            Azure.ResourceManager.Resources.TenantResource? tenantResource = null;
            await foreach (var t in tenant)
            {
                tenantResource = t;
                break;
            }

            if (tenantResource == null)
                return results;

            do
            {
                ct.ThrowIfCancellationRequested();

                var options = new ResourceQueryRequestOptions
                {
                    Top = maxResults
                };
                if (skipToken != null)
                    options.SkipToken = skipToken;

                var queryContent = new ResourceQueryContent(kqlQuery)
                {
                    Options = options
                };
                foreach (var sub in subList)
                    queryContent.Subscriptions.Add(sub);

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var response = await tenantResource.GetResourcesAsync(queryContent, ct);
                        var queryResult = response.Value;

                        if (queryResult?.Data is { } data)
                        {
                            using var doc = JsonDocument.Parse(data.ToString());
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var element in doc.RootElement.EnumerateArray())
                                    results.Add(element.Clone());
                            }
                            else if (doc.RootElement.TryGetProperty("rows", out var rows)
                                     && rows.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var row in rows.EnumerateArray())
                                    results.Add(row.Clone());
                            }
                        }

                        skipToken = queryResult?.SkipToken;
                        break; // success
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 429 && attempt < maxRetries)
                    {
                        int wait = Math.Min(10 * (int)Math.Pow(2, attempt), 60);
                        StatusCallback?.Invoke($"Resource Graph rate limited - waiting {wait}s...");
                        await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        StatusCallback?.Invoke($"Resource Graph query error: {ex.Message}");
                        skipToken = null;
                        break;
                    }
                }
            }
            while (skipToken != null && results.Count < maxResults * 10);

            return results;
        }

        /// <summary>
        /// Convenience helper: queries and returns results, logging status.
        /// </summary>
        public async Task<List<JsonElement>> QuerySafeAsync(
            string kqlQuery,
            IEnumerable<string> subscriptionIds,
            string? statusLabel = null,
            CancellationToken ct = default)
        {
            try
            {
                if (statusLabel != null)
                    StatusCallback?.Invoke(statusLabel);

                return await QueryAsync(kqlQuery, subscriptionIds, ct: ct);
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Resource Graph error ({statusLabel}): {ex.Message}");
                return new List<JsonElement>();
            }
        }
    }
}
