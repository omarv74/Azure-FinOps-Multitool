using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Queries Cost Management for actual month-to-date and forecasted costs.
    /// Tries management-group scope first, falls back to per-subscription.
    /// Equivalent to Get-CostData.ps1.
    /// </summary>
    public class CostManagementService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        // Shared state: if MG-scope fails once, skip for all subsequent calls
        private bool _mgScopeFailed;

        public CostManagementService(AzureRestService rest) => _rest = rest;

        public async Task<Dictionary<string, SubscriptionCost>> GetCostDataAsync(
            string tenantId,
            IList<SubscriptionInfo> subs,
            CancellationToken ct = default)
        {
            if (_mgScopeFailed)
            {
                StatusCallback?.Invoke("Querying actual costs (per-subscription)...");
                return await GetCostDataPerSubscriptionAsync(subs, ct);
            }

            var costMap = new Dictionary<string, SubscriptionCost>(StringComparer.OrdinalIgnoreCase);

            // ── Actual Cost (Month-to-Date) at MG scope ──────────────────────
            StatusCallback?.Invoke("Querying actual costs (MG scope)...");
            try
            {
                var actualBody = BuildCostQuery("ActualCost", "MonthToDate", "None",
                    new[] { new { type = "Dimension", name = "SubscriptionId" } });

                var mgPath = $"/providers/Microsoft.Management/managementGroups/{tenantId}" +
                             "/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                var doc = await _rest.PostAsync(mgPath, actualBody, ct);

                if (doc == null)
                    throw new InvalidOperationException("MG-scope cost query returned no data.");

                ParseCostRows(doc.RootElement, costMap, isActual: true);
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"MG-scope cost failed: {ex.Message} – falling back to per-sub.");
                _mgScopeFailed = true;
                return await GetCostDataPerSubscriptionAsync(subs, ct);
            }

            // ── Forecast (remaining days of month) at MG scope ───────────────
            try
            {
                StatusCallback?.Invoke("Querying forecast costs (MG scope)...");
                var now = DateTime.UtcNow;
                var monthEnd = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1);

                var forecastBody = BuildForecastQuery(now, monthEnd);
                var fPath = $"/providers/Microsoft.Management/managementGroups/{tenantId}" +
                            "/providers/Microsoft.CostManagement/forecast?api-version=2023-11-01";
                var fDoc = await _rest.PostAsync(fPath, forecastBody, ct);

                if (fDoc != null)
                    ParseForecastRows(fDoc.RootElement, costMap);
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Forecast failed (non-critical): {ex.Message}");
            }

            return costMap;
        }

        private async Task<Dictionary<string, SubscriptionCost>> GetCostDataPerSubscriptionAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct)
        {
            var costMap = new Dictionary<string, SubscriptionCost>(StringComparer.OrdinalIgnoreCase);
            bool skipForecast = subs.Count > 50;

            int i = 0;
            foreach (var sub in subs)
            {
                i++;
                if (i % Math.Max(1, subs.Count / 10) == 0 || i == 1 || i == subs.Count)
                    StatusCallback?.Invoke($"Querying costs ({i}/{subs.Count} subs)...");

                ct.ThrowIfCancellationRequested();
                try
                {
                    var body = BuildCostQuery("ActualCost", "MonthToDate", "None", null);
                    var path = $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                    var doc = await _rest.PostAsync(path, body, ct);

                    double actual = 0; string currency = "USD";
                    if (doc?.RootElement.TryGetProperty("properties", out var p) == true &&
                        p.TryGetProperty("rows", out var rows) &&
                        rows.GetArrayLength() > 0)
                    {
                        var row = rows[0];
                        actual   = row[0].GetDouble();
                        currency = row.GetArrayLength() > 1 ? row[1].GetString() ?? "USD" : "USD";
                    }

                    costMap[sub.Id] = new SubscriptionCost
                    {
                        Actual   = Math.Round(actual, 2),
                        Forecast = Math.Round(actual, 2),
                        Currency = currency
                    };

                    if (!skipForecast)
                    {
                        try
                        {
                            var now = DateTime.UtcNow;
                            var monthEnd = new DateTime(now.Year, now.Month, 1).AddMonths(1).AddDays(-1);
                            var fBody = BuildForecastQuery(now, monthEnd);
                            var fPath = $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement/forecast?api-version=2023-11-01";
                            var fDoc = await _rest.PostAsync(fPath, fBody, ct);
                            if (fDoc?.RootElement.TryGetProperty("properties", out var fp) == true &&
                                fp.TryGetProperty("rows", out var frows) &&
                                frows.GetArrayLength() > 0)
                            {
                                double fAmount = frows[0][0].GetDouble();
                                costMap[sub.Id].Forecast = Math.Round(actual + fAmount, 2);
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    StatusCallback?.Invoke($"Cost query failed for {sub.Name}: {ex.Message}");
                }
            }

            return costMap;
        }

        private static string BuildCostQuery(string type, string timeframe, string granularity, object[]? grouping)
        {
            var obj = new System.Collections.Generic.Dictionary<string, object>
            {
                ["type"]      = type,
                ["timeframe"] = timeframe,
                ["dataset"]   = new Dictionary<string, object>
                {
                    ["granularity"] = granularity,
                    ["aggregation"] = new Dictionary<string, object>
                    {
                        ["totalCost"] = new Dictionary<string, string>
                            { ["name"] = "Cost", ["function"] = "Sum" }
                    },
                    ["grouping"] = grouping ?? Array.Empty<object>()
                }
            };
            return JsonSerializer.Serialize(obj);
        }

        private static string BuildForecastQuery(DateTime from, DateTime to)
        {
            var obj = new Dictionary<string, object>
            {
                ["type"]      = "ActualCost",
                ["timeframe"] = "Custom",
                ["timePeriod"] = new Dictionary<string, string>
                {
                    ["from"] = from.ToString("yyyy-MM-dd"),
                    ["to"]   = to.ToString("yyyy-MM-dd")
                },
                ["dataset"] = new Dictionary<string, object>
                {
                    ["granularity"] = "None",
                    ["aggregation"] = new Dictionary<string, object>
                    {
                        ["totalCost"] = new Dictionary<string, string>
                            { ["name"] = "Cost", ["function"] = "Sum" }
                    },
                    ["grouping"] = new[] { new { type = "Dimension", name = "SubscriptionId" } }
                },
                ["includeActualCost"] = true,
                ["includeFreshPartialCost"] = false
            };
            return JsonSerializer.Serialize(obj);
        }

        private static void ParseCostRows(
            JsonElement root, Dictionary<string, SubscriptionCost> costMap, bool isActual)
        {
            if (!root.TryGetProperty("properties", out var props)) return;
            if (!props.TryGetProperty("rows", out var rows)) return;

            foreach (var row in rows.EnumerateArray())
            {
                if (row.GetArrayLength() < 2) continue;
                double amount = Math.Round(row[0].GetDouble(), 2);
                string subId   = row[1].GetString() ?? string.Empty;
                string currency = row.GetArrayLength() > 2 ? row[2].GetString() ?? "USD" : "USD";

                if (!costMap.TryGetValue(subId, out var entry))
                {
                    entry = new SubscriptionCost { Currency = currency };
                    costMap[subId] = entry;
                }

                if (isActual) entry.Actual = amount;
                entry.Currency = currency;
            }
        }

        private static void ParseForecastRows(
            JsonElement root, Dictionary<string, SubscriptionCost> costMap)
        {
            if (!root.TryGetProperty("properties", out var props)) return;
            if (!props.TryGetProperty("rows", out var rows)) return;

            foreach (var row in rows.EnumerateArray())
            {
                if (row.GetArrayLength() < 2) continue;
                double amount = Math.Round(row[0].GetDouble(), 2);
                string subId  = row[1].GetString() ?? string.Empty;

                if (costMap.TryGetValue(subId, out var entry))
                    entry.Forecast = Math.Round(entry.Actual + amount, 2);
                else
                    costMap[subId] = new SubscriptionCost { Forecast = amount };
            }
        }
    }
}
