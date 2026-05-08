using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Queries Azure Budgets API per subscription and correlates with cost data.
    /// Equivalent to Get-BudgetStatus.ps1.
    /// </summary>
    public class BudgetService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public BudgetService(AzureRestService rest) => _rest = rest;

        public async Task<BudgetResult> GetBudgetStatusAsync(
            IList<SubscriptionInfo> subs,
            Dictionary<string, SubscriptionCost>? costData = null,
            CancellationToken ct = default)
        {
            int subCount = subs.Count;
            StatusCallback?.Invoke($"Querying budget status ({subCount} subs)...");

            var budgets          = new List<BudgetItem>();
            int subsWithBudget   = 0;
            int subsWithoutBudget = 0;
            bool sampled         = false;

            // For large tenants, sample first to see if budgets exist
            IList<SubscriptionInfo> subsToQuery = subs;
            if (subCount > 50)
            {
                int sampleSize = Math.Min(10, subCount);
                StatusCallback?.Invoke($"Large tenant: sampling {sampleSize} of {subCount} subs for budgets...");
                int sampleHits = 0;

                for (int si = 0; si < sampleSize; si++)
                {
                    ct.ThrowIfCancellationRequested();
                    var sub = subs[si];
                    try
                    {
                        var doc = await _rest.GetAsync(
                            $"/subscriptions/{sub.Id}/providers/Microsoft.Consumption/budgets?api-version=2023-05-01",
                            ct);
                        if (doc?.RootElement.TryGetProperty("value", out var v) == true && v.GetArrayLength() > 0)
                            sampleHits++;
                    }
                    catch { }
                }

                if (sampleHits == 0)
                {
                    sampled = true;
                    subsWithoutBudget = subCount;
                    subsToQuery = Array.Empty<SubscriptionInfo>();
                    StatusCallback?.Invoke($"No budgets found in sample – skipping remaining {subCount - sampleSize} subs.");
                }
            }

            int i = 0;
            foreach (var sub in subsToQuery)
            {
                i++;
                if (i % Math.Max(1, subsToQuery.Count / 10) == 0 || i == 1 || i == subsToQuery.Count)
                    StatusCallback?.Invoke($"Querying budgets ({i}/{subsToQuery.Count} subs)...");

                ct.ThrowIfCancellationRequested();
                try
                {
                    var doc = await _rest.GetAsync(
                        $"/subscriptions/{sub.Id}/providers/Microsoft.Consumption/budgets?api-version=2023-05-01",
                        ct);

                    if (doc?.RootElement.TryGetProperty("value", out var val) == true &&
                        val.GetArrayLength() > 0)
                    {
                        subsWithBudget++;
                        foreach (var budget in val.EnumerateArray())
                        {
                            var bp = budget.TryGetProperty("properties", out var bpp) ? bpp : default;
                            string budgetName = budget.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;

                            double amount   = GetDouble(bp, "amount");
                            string grain    = GetStr(bp, "timeGrain");
                            string category = GetStr(bp, "category");

                            double actualSpend = 0, forecast = 0;
                            string currency = "USD";
                            if (costData?.TryGetValue(sub.Id, out var cd) == true)
                            {
                                actualSpend = cd.Actual;
                                forecast    = cd.Forecast;
                                currency    = cd.Currency;
                            }

                            double pctUsed    = amount > 0 ? Math.Round(actualSpend / amount * 100, 1) : 0;
                            double pctForecast = amount > 0 ? Math.Round(forecast / amount * 100, 1) : 0;

                            string risk = pctForecast > 100 ? "Over Budget"
                                        : pctForecast > 90  ? "At Risk"
                                        : pctForecast > 75  ? "Watch"
                                        : "On Track";

                            // Parse notifications
                            var thresholds    = new List<string>();
                            var contactEmails = new List<string>();
                            var contactRoles  = new List<string>();

                            if (bp.TryGetProperty("notifications", out var notifs) &&
                                notifs.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var notif in notifs.EnumerateObject())
                                {
                                    var np = notif.Value;
                                    string threshold = GetStr(np, "threshold");
                                    string op        = GetStr(np, "operator");
                                    if (!string.IsNullOrEmpty(threshold))
                                        thresholds.Add($"{threshold}% ({op})");

                                    if (np.TryGetProperty("contactEmails", out var emails) &&
                                        emails.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var e in emails.EnumerateArray())
                                        {
                                            var es = e.GetString();
                                            if (es != null && !contactEmails.Contains(es))
                                                contactEmails.Add(es);
                                        }
                                    }
                                    if (np.TryGetProperty("contactRoles", out var roles) &&
                                        roles.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var r in roles.EnumerateArray())
                                        {
                                            var rs = r.GetString();
                                            if (rs != null && !contactRoles.Contains(rs))
                                                contactRoles.Add(rs);
                                        }
                                    }
                                }
                            }

                            // Parse tag filters
                            var tagFilters = new List<string>();
                            if (bp.TryGetProperty("filter", out var filter))
                            {
                                if (filter.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var tag in tags.EnumerateObject())
                                    {
                                        var vals = tag.Value.TryGetProperty("values", out var v2)
                                            ? GetStringArray(v2) : new List<string>();
                                        tagFilters.Add($"{tag.Name}={string.Join("|", vals)}");
                                    }
                                }
                            }

                            budgets.Add(new BudgetItem
                            {
                                Subscription   = sub.Name,
                                SubscriptionId = sub.Id,
                                BudgetName     = budgetName,
                                Amount         = amount,
                                TimeGrain      = grain,
                                Category       = category,
                                ActualSpend    = Math.Round(actualSpend, 2),
                                Forecast       = Math.Round(forecast, 2),
                                PctUsed        = pctUsed,
                                PctForecast    = pctForecast,
                                Risk           = risk,
                                Thresholds     = string.Join(", ", thresholds),
                                ContactEmails  = string.Join(", ", contactEmails),
                                ContactRoles   = string.Join(", ", contactRoles),
                                TagFilter      = string.Join("; ", tagFilters),
                                Currency       = currency
                            });
                        }
                    }
                    else
                    {
                        subsWithoutBudget++;
                    }
                }
                catch
                {
                    subsWithoutBudget++;
                }
            }

            int overBudget = 0, atRisk = 0;
            foreach (var b in budgets)
            {
                if (b.Risk == "Over Budget") overBudget++;
                else if (b.Risk == "At Risk")  atRisk++;
            }

            return new BudgetResult
            {
                Budgets           = budgets,
                TotalBudgets      = budgets.Count,
                SubsWithBudget    = subsWithBudget,
                SubsWithoutBudget = subsWithoutBudget,
                OverBudgetCount   = overBudget,
                AtRiskCount       = atRisk,
                HasData           = budgets.Count > 0,
                Sampled           = sampled,
                BudgetCoverage    = subs.Count > 0
                    ? Math.Round((double)subsWithBudget / subs.Count * 100, 1) : 0
            };
        }

        public async Task<(bool Success, string Message)> DeployBudgetAsync(
            string subscriptionId,
            string budgetName,
            double amount,
            string timeGrain,
            IEnumerable<string> contactEmails,
            IEnumerable<double> thresholds,
            CancellationToken ct = default)
        {
            StatusCallback?.Invoke($"Deploying budget '{budgetName}' to subscription {subscriptionId}...");

            var now = DateTime.UtcNow;
            var startDate = new DateTime(now.Year, now.Month, 1);

            var notifications = new Dictionary<string, object>();
            int idx = 1;
            foreach (var t in thresholds)
            {
                notifications[$"Actual_{t}Pct"] = new
                {
                    enabled         = true,
                    @operator       = "GreaterThan",
                    threshold       = t,
                    contactEmails   = contactEmails,
                    contactRoles    = new[] { "Owner" }
                };
                idx++;
            }

            var body = JsonSerializer.Serialize(new
            {
                properties = new
                {
                    category      = "Cost",
                    amount,
                    timeGrain,
                    timePeriod    = new { startDate = startDate.ToString("yyyy-MM-dd") },
                    notifications
                }
            });

            var path = $"/subscriptions/{subscriptionId}/providers/Microsoft.Consumption" +
                       $"/budgets/{budgetName}?api-version=2023-05-01";
            var doc = await _rest.PutAsync(path, body, ct);

            return doc != null
                ? (true, $"Budget '{budgetName}' deployed to subscription {subscriptionId}")
                : (false, "Budget deployment failed – check permissions.");
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        private static double GetDouble(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            }
            return 0;
        }

        private static List<string> GetStringArray(JsonElement el)
        {
            var list = new List<string>();
            if (el.ValueKind == JsonValueKind.Array)
                foreach (var item in el.EnumerateArray())
                {
                    var s = item.GetString(); if (s != null) list.Add(s);
                }
            return list;
        }
    }
}
