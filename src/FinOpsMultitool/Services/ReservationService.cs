using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Pulls RI and Savings Plan recommendations from Azure Advisor and
    /// the Reservation Recommendation API.
    /// Equivalent to Get-ReservationAdvice.ps1.
    /// </summary>
    public class ReservationService
    {
        private readonly AzureRestService _rest;
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public ReservationService(AzureRestService rest, ResourceGraphService graph)
        {
            _rest  = rest;
            _graph = graph;
        }

        public async Task<ReservationResult> GetReservationAdviceAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            var subIds   = GetSubIds(subs);
            var subNames = BuildSubNameMap(subs);
            var advisorRecs = new List<ReservationAdvice>();

            // ── Advisor via Resource Graph ────────────────────────────────────
            StatusCallback?.Invoke("Querying RI/SP recommendations via Resource Graph...");
            var query = @"
advisorresources
| where type == 'microsoft.advisor/recommendations'
| where properties.category == 'Cost'
| where properties.shortDescription.problem matches regex '(?i)reserv|savings plan|reserved instance'
     or properties.shortDescription.solution matches regex '(?i)reserv|savings plan|reserved instance'
| project subscriptionId,
    shortDescriptionProblem  = tostring(properties.shortDescription.problem),
    shortDescriptionSolution = tostring(properties.shortDescription.solution),
    impact          = tostring(properties.impact),
    impactedField   = tostring(properties.impactedField),
    impactedValue   = tostring(properties.impactedValue),
    annualSavings   = tostring(properties.extendedProperties.annualSavingsAmount),
    savingsCurrency = tostring(properties.extendedProperties.savingsCurrency),
    term            = tostring(properties.extendedProperties.term)";

            bool graphFailed = false;
            try
            {
                var rows = await _graph.QueryAsync(query, subIds, ct: ct);
                foreach (var row in rows)
                {
                    string subId = GetStr(row, "subscriptionId");
                    double? savings = ParseDouble(GetStr(row, "annualSavings"));
                    advisorRecs.Add(new ReservationAdvice
                    {
                        Subscription   = subNames.GetValueOrDefault(subId, subId),
                        SubscriptionId = subId,
                        Problem        = GetStr(row, "shortDescriptionProblem"),
                        Solution       = GetStr(row, "shortDescriptionSolution"),
                        Impact         = GetStr(row, "impact"),
                        Category       = "Reservation / Savings Plan",
                        ResourceType   = GetStr(row, "impactedField"),
                        ResourceName   = GetStr(row, "impactedValue"),
                        AnnualSavings  = savings,
                        Currency       = GetStr(row, "savingsCurrency"),
                        Term           = GetStr(row, "term")
                    });
                }
            }
            catch { graphFailed = true; }

            // ── Per-subscription REST fallback ────────────────────────────────
            if (graphFailed)
            {
                StatusCallback?.Invoke("Falling back to per-subscription Advisor REST...");
                foreach (var sub in subs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var doc = await _rest.GetAsync(
                            $"/subscriptions/{sub.Id}/providers/Microsoft.Advisor/recommendations" +
                            "?api-version=2023-01-01&$filter=Category eq 'Cost'", ct);

                        if (doc?.RootElement.TryGetProperty("value", out var val) == true)
                        {
                            foreach (var item in val.EnumerateArray())
                            {
                                var p = item.TryGetProperty("properties", out var pp) ? pp : default;
                                string prob = GetPropStr(p, "shortDescription", "problem");
                                string sol  = GetPropStr(p, "shortDescription", "solution");
                                if (!IsReservationRec(prob, sol)) continue;

                                var ext = p.TryGetProperty("extendedProperties", out var ep) ? ep : default;
                                advisorRecs.Add(new ReservationAdvice
                                {
                                    Subscription   = sub.Name,
                                    SubscriptionId = sub.Id,
                                    Problem        = prob,
                                    Solution       = sol,
                                    Impact         = GetStr(p, "impact"),
                                    Category       = "Reservation / Savings Plan",
                                    ResourceType   = GetStr(p, "impactedField"),
                                    ResourceName   = GetStr(p, "impactedValue"),
                                    AnnualSavings  = ParseDouble(GetStr(ext, "annualSavingsAmount")),
                                    Currency       = GetStr(ext, "savingsCurrency"),
                                    Term           = GetStr(ext, "term")
                                });
                            }
                        }
                    }
                    catch { }
                }
            }

            // ── Reservation Recommendation API ────────────────────────────────
            var reservationRecs = new List<ReservationRecommendation>();
            StatusCallback?.Invoke("Querying Reservation Recommendation API...");
            try
            {
                var doc = await _rest.GetAsync(
                    "/providers/Microsoft.Consumption/reservationRecommendations" +
                    "?api-version=2023-05-01&$filter=properties/scope eq 'Shared' and properties/lookBackPeriod eq 'Last30Days'",
                    ct);

                if (doc?.RootElement.TryGetProperty("value", out var val) == true)
                {
                    foreach (var item in val.EnumerateArray())
                    {
                        var p = item.TryGetProperty("properties", out var pp) ? pp : default;
                        if (p.ValueKind == JsonValueKind.Undefined) continue;

                        var skuEl = p.TryGetProperty("skuProperties", out var sp) ? sp : default;
                        reservationRecs.Add(new ReservationRecommendation
                        {
                            ResourceType   = GetStr(p, "resourceType"),
                            SKU            = GetStr(skuEl, "name"),
                            RecommendedQty = ParseDouble(GetStr(p, "recommendedQuantity")),
                            Term           = GetStr(p, "term"),
                            CostWithoutRI  = ParseDouble(GetStr(p, "costWithNoReservedInstances")),
                            CostWithRI     = ParseDouble(GetStr(p, "totalCostWithReservedInstances")),
                            NetSavings     = ParseDouble(GetStr(p, "netSavings")),
                            Currency       = GetStr(p, "currencyCode"),
                            Scope          = GetStr(p, "scope"),
                            LookBackPeriod = GetStr(p, "lookBackPeriod")
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Reservation recommendation API failed: {ex.Message}");
            }

            double totalSavings = 0;
            foreach (var r in advisorRecs)
                if (r.AnnualSavings.HasValue) totalSavings += r.AnnualSavings.Value;

            return new ReservationResult
            {
                AdvisorRecommendations     = advisorRecs,
                ReservationRecommendations = reservationRecs,
                TotalAdvisorCount          = advisorRecs.Count,
                EstimatedAnnualSavings     = Math.Round(totalSavings, 2)
            };
        }

        private static bool IsReservationRec(string problem, string solution)
        {
            const string pattern = "reserv|savings plan|reserved instance";
            return System.Text.RegularExpressions.Regex.IsMatch(problem + " " + solution, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static List<string> GetSubIds(IList<SubscriptionInfo> subs)
        {
            var ids = new List<string>();
            foreach (var s in subs) ids.Add(s.Id);
            return ids;
        }

        private static Dictionary<string, string> BuildSubNameMap(IList<SubscriptionInfo> subs)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs) map[s.Id] = s.Name;
            return map;
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        private static string GetPropStr(JsonElement el, string prop1, string prop2)
        {
            if (el.TryGetProperty(prop1, out var p1) && p1.TryGetProperty(prop2, out var v))
                return v.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static double? ParseDouble(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return Math.Round(d, 2);
            return null;
        }
    }
}
