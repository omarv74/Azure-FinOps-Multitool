using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Identifies hot-tier storage accounts with low activity that could
    /// be moved to Cool or Archive to reduce costs.
    /// Equivalent to Get-StorageTierAdvice.ps1.
    /// </summary>
    public class StorageTierService
    {
        private readonly AzureRestService _rest;
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public StorageTierService(AzureRestService rest, ResourceGraphService graph)
        {
            _rest  = rest;
            _graph = graph;
        }

        public async Task<StorageTierResult> GetStorageTierAdviceAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Scanning storage tier optimization opportunities...");
            var subIds = GetSubIds(subs);
            var results = new List<StorageTierItem>();

            // ── Find hot-tier storage accounts ────────────────────────────────
            var query = @"
resources
| where type =~ 'microsoft.storage/storageaccounts'
| where properties.accessTier =~ 'Hot' or isnull(properties.accessTier)
| project name, resourceGroup, subscriptionId, location,
          kind, sku = sku.name,
          accessTier = tostring(properties.accessTier)";

            var hotAccounts = await _graph.QuerySafeAsync(query, subIds, "Querying hot-tier storage accounts...", ct);
            StatusCallback?.Invoke($"Found {hotAccounts.Count} hot-tier storage accounts. Checking metrics...");

            // Get bearer token for direct ARM metrics calls
            string token = await _rest.GetBearerTokenAsync(ct);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var now          = DateTime.UtcNow;
            var thirtyDaysAgo = now.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nowStr        = now.ToString("yyyy-MM-ddTHH:mm:ssZ");

            int idx = 0;
            foreach (var sa in hotAccounts)
            {
                idx++;
                ct.ThrowIfCancellationRequested();

                if (idx % Math.Max(1, hotAccounts.Count / 10) == 0 || idx == 1)
                    StatusCallback?.Invoke($"Checking storage metrics ({idx}/{hotAccounts.Count})...");

                string name      = GetStr(sa, "name");
                string rg        = GetStr(sa, "resourceGroup");
                string subId     = GetStr(sa, "subscriptionId");
                string location  = GetStr(sa, "location");
                string kind      = GetStr(sa, "kind");
                string tier      = GetStr(sa, "accessTier");

                string scope = $"/subscriptions/{subId}/resourceGroups/{rg}" +
                               $"/providers/Microsoft.Storage/storageAccounts/{name}";
                try
                {
                    // Query transaction count over last 30 days
                    long totalTx = 0;
                    double capacityGB = 0;

                    var txUri = $"{_rest.BaseUrl}{scope}/blobServices/default/providers/Microsoft.Insights/metrics" +
                                $"?api-version=2023-10-01&metricnames=Transactions" +
                                $"&timespan={thirtyDaysAgo}/{nowStr}&aggregation=Total&interval=P30D";
                    var txResp = await http.GetAsync(txUri, ct);
                    if (txResp.IsSuccessStatusCode)
                    {
                        var txContent = await txResp.Content.ReadAsStringAsync(ct);
                        using var txDoc = JsonDocument.Parse(txContent);
                        totalTx = SumMetricTotal(txDoc.RootElement);
                    }

                    // Query blob capacity
                    var capUri = $"{_rest.BaseUrl}{scope}/blobServices/default/providers/Microsoft.Insights/metrics" +
                                 $"?api-version=2023-10-01&metricnames=BlobCapacity" +
                                 $"&timespan={thirtyDaysAgo}/{nowStr}&aggregation=Average&interval=P30D";
                    var capResp = await http.GetAsync(capUri, ct);
                    if (capResp.IsSuccessStatusCode)
                    {
                        var capContent = await capResp.Content.ReadAsStringAsync(ct);
                        using var capDoc = JsonDocument.Parse(capContent);
                        double capBytes = MaxMetricAverage(capDoc.RootElement);
                        capacityGB = Math.Round(capBytes / 1_073_741_824, 2);
                    }

                    string? recommendation = null;
                    if (totalTx == 0 && capacityGB > 0)          recommendation = "Archive";
                    else if (totalTx < 100 && capacityGB > 0)    recommendation = "Archive";
                    else if (totalTx < 1000 && capacityGB > 1)   recommendation = "Cool";

                    if (recommendation != null)
                    {
                        results.Add(new StorageTierItem
                        {
                            Name           = name,
                            ResourceGroup  = rg,
                            SubscriptionId = subId,
                            Location       = location,
                            Kind           = kind,
                            Tier           = string.IsNullOrEmpty(tier) ? "Hot" : tier,
                            SizeGB         = capacityGB,
                            TransactionCount = totalTx,
                            Recommendation = recommendation
                        });
                    }
                }
                catch { }
            }

            return new StorageTierResult
            {
                StorageAccounts = results,
                Count           = results.Count,
                HasData         = results.Count > 0
            };
        }

        private static long SumMetricTotal(JsonElement root)
        {
            long total = 0;
            if (!root.TryGetProperty("value", out var metrics)) return total;
            foreach (var metric in metrics.EnumerateArray())
            {
                if (!metric.TryGetProperty("timeseries", out var ts)) continue;
                foreach (var series in ts.EnumerateArray())
                {
                    if (!series.TryGetProperty("data", out var data)) continue;
                    foreach (var dp in data.EnumerateArray())
                    {
                        if (dp.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                            total += (long)t.GetDouble();
                    }
                }
            }
            return total;
        }

        private static double MaxMetricAverage(JsonElement root)
        {
            double max = 0;
            if (!root.TryGetProperty("value", out var metrics)) return max;
            foreach (var metric in metrics.EnumerateArray())
            {
                if (!metric.TryGetProperty("timeseries", out var ts)) continue;
                foreach (var series in ts.EnumerateArray())
                {
                    if (!series.TryGetProperty("data", out var data)) continue;
                    foreach (var dp in data.EnumerateArray())
                    {
                        if (dp.TryGetProperty("average", out var a) && a.ValueKind == JsonValueKind.Number)
                        {
                            double v = a.GetDouble();
                            if (v > max) max = v;
                        }
                    }
                }
            }
            return max;
        }

        private static List<string> GetSubIds(IList<SubscriptionInfo> subs)
        {
            var ids = new List<string>();
            foreach (var s in subs)
                ids.Add(s.Id);
            return ids;
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;
    }
}
