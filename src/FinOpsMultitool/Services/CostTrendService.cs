using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Queries Cost Management for 6-month rolling cost trend (monthly totals).
    /// Tries MG-scope first, then per-subscription.
    /// Equivalent to Get-CostTrend.ps1.
    /// </summary>
    public class CostTrendService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }
        private bool _mgScopeFailed;

        public CostTrendService(AzureRestService rest) => _rest = rest;

        public async Task<CostTrendResult> GetCostTrendAsync(
            string tenantId,
            IList<SubscriptionInfo> subs,
            CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Querying 6-month cost trend...");

            var now       = DateTime.UtcNow;
            var startDate = new DateTime(now.Year, now.Month, 1).AddMonths(-6);
            var body = BuildQuery(startDate, now);

            // ── Try MG scope first ────────────────────────────────────────────
            if (!_mgScopeFailed && !string.IsNullOrEmpty(tenantId))
            {
                try
                {
                    var mgPath = $"/providers/Microsoft.Management/managementGroups/{tenantId}" +
                                 "/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                    var doc = await _rest.PostAsync(mgPath, body, ct);
                    if (doc != null)
                    {
                        var months = ParseMonthRows(doc.RootElement);
                        if (months.Count > 0)
                            return new CostTrendResult { Months = months, HasData = true };
                    }
                }
                catch
                {
                    _mgScopeFailed = true;
                }
            }

            // ── Per-subscription aggregation ──────────────────────────────────
            var aggByMonth = new Dictionary<string, CostTrendMonth>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var sub in subs)
            {
                i++;
                ct.ThrowIfCancellationRequested();

                if (i % Math.Max(1, subs.Count / 10) == 0 || i == 1 || i == subs.Count)
                    StatusCallback?.Invoke($"Querying cost trend ({i}/{subs.Count} subs)...");

                try
                {
                    var subPath = $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                    var doc = await _rest.PostAsync(subPath, body, ct);
                    if (doc == null) continue;

                    var subMonths = ParseMonthRows(doc.RootElement);
                    foreach (var m in subMonths)
                    {
                        if (!aggByMonth.TryGetValue(m.Month, out var existing))
                        {
                            aggByMonth[m.Month] = new CostTrendMonth
                            {
                                Month    = m.Month,
                                Cost     = m.Cost,
                                Currency = m.Currency
                            };
                        }
                        else
                        {
                            existing.Cost += m.Cost;
                        }
                    }
                }
                catch { }
            }

            if (aggByMonth.Count == 0)
                return new CostTrendResult { Months = new List<CostTrendMonth>(), HasData = false };

            // Sort months chronologically
            var sortedMonths = new List<CostTrendMonth>(aggByMonth.Values);
            sortedMonths.Sort((a, b) =>
            {
                if (DateTime.TryParse("1 " + a.Month, out var da) &&
                    DateTime.TryParse("1 " + b.Month, out var db))
                    return da.CompareTo(db);
                return string.Compare(a.Month, b.Month, StringComparison.Ordinal);
            });

            foreach (var m in sortedMonths)
                m.Cost = Math.Round(m.Cost, 2);

            return new CostTrendResult { Months = sortedMonths, HasData = true };
        }

        private static string BuildQuery(DateTime from, DateTime to)
        {
            return JsonSerializer.Serialize(new
            {
                type = "ActualCost",
                timeframe = "Custom",
                timePeriod = new
                {
                    from = from.ToString("yyyy-MM-dd"),
                    to   = to.ToString("yyyy-MM-dd")
                },
                dataset = new
                {
                    granularity = "Monthly",
                    aggregation = new
                    {
                        totalCost = new { name = "Cost", function = "Sum" }
                    }
                }
            });
        }

        private static List<CostTrendMonth> ParseMonthRows(JsonElement root)
        {
            var months = new List<CostTrendMonth>();
            if (!root.TryGetProperty("properties", out var props)) return months;
            if (!props.TryGetProperty("rows", out var rows)) return months;

            int costIdx = 0, dateIdx = 1, currIdx = 2;

            // Detect column order from "columns" metadata
            if (props.TryGetProperty("columns", out var cols))
            {
                for (int ci = 0; ci < cols.GetArrayLength(); ci++)
                {
                    var cname = cols[ci].TryGetProperty("name", out var nm) ? nm.GetString()?.ToLowerInvariant() : null;
                    var ctype = cols[ci].TryGetProperty("type", out var tp) ? tp.GetString()?.ToLowerInvariant() : null;
                    if (cname?.Contains("cost") == true) costIdx = ci;
                    else if (ctype == "datetime" || cname?.Contains("date") == true) dateIdx = ci;
                    else if (cname?.Contains("currency") == true) currIdx = ci;
                }
            }

            foreach (var row in rows.EnumerateArray())
            {
                int len = row.GetArrayLength();
                if (len == 0) continue;

                double cost = row[costIdx].ValueKind == JsonValueKind.Number
                    ? row[costIdx].GetDouble() : 0;

                string dateStr = dateIdx < len ? row[dateIdx].GetString() ?? string.Empty : string.Empty;
                string currency = currIdx < len ? row[currIdx].GetString() ?? "USD" : "USD";

                // Parse date: can be "20240101" (yyyyMMdd) or ISO string
                DateTime date;
                if (dateStr.Length == 8 && long.TryParse(dateStr, out _))
                {
                    if (!DateTime.TryParseExact(dateStr, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out date))
                        continue;
                }
                else if (!DateTime.TryParse(dateStr, out date))
                    continue;

                months.Add(new CostTrendMonth
                {
                    Month    = date.ToString("MMM yyyy"),
                    Cost     = cost,
                    Currency = currency
                });
            }

            return months;
        }
    }
}
