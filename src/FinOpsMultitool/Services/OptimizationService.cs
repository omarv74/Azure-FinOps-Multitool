using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Pulls all Advisor cost optimization recommendations.
    /// Categorizes by type: Rightsize, Shutdown, Delete, Modernize.
    /// Equivalent to Get-OptimizationAdvice.ps1.
    /// </summary>
    public class OptimizationService
    {
        private readonly AzureRestService _rest;
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public OptimizationService(AzureRestService rest, ResourceGraphService graph)
        {
            _rest  = rest;
            _graph = graph;
        }

        public async Task<OptimizationResult> GetOptimizationAdviceAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            var subIds   = GetSubIds(subs);
            var subNames = BuildSubNameMap(subs);
            var allRecs  = new List<OptimizationRecommendation>();

            StatusCallback?.Invoke("Querying Advisor cost recommendations via Resource Graph...");

            var query = @"
advisorresources
| where type == 'microsoft.advisor/recommendations'
| where properties.category == 'Cost'
| project subscriptionId,
    shortDescriptionProblem  = tostring(properties.shortDescription.problem),
    shortDescriptionSolution = tostring(properties.shortDescription.solution),
    impact          = tostring(properties.impact),
    impactedField   = tostring(properties.impactedField),
    impactedValue   = tostring(properties.impactedValue),
    annualSavings   = tostring(properties.extendedProperties.annualSavingsAmount),
    savingsAmount   = tostring(properties.extendedProperties.savingsAmount),
    savingsCurrency = tostring(properties.extendedProperties.savingsCurrency)";

            bool graphFailed = false;
            try
            {
                var rows = await _graph.QueryAsync(query, subIds, ct: ct);
                foreach (var row in rows)
                {
                    string problem  = GetStr(row, "shortDescriptionProblem");
                    string solution = GetStr(row, "shortDescriptionSolution");

                    // Skip reservation recs (handled by ReservationService)
                    if (Regex.IsMatch(problem, "reserv|savings plan", RegexOptions.IgnoreCase))
                        continue;

                    string subId   = GetStr(row, "subscriptionId");
                    double? savings = ParseDouble(GetStr(row, "annualSavings"))
                                   ?? ParseDouble(GetStr(row, "savingsAmount"));

                    allRecs.Add(new OptimizationRecommendation
                    {
                        Subscription   = subNames.GetValueOrDefault(subId, subId),
                        SubscriptionId = subId,
                        Category       = Categorize(problem, solution),
                        Impact         = GetStr(row, "impact"),
                        Problem        = problem,
                        Solution       = solution,
                        ResourceType   = GetStr(row, "impactedField"),
                        ResourceName   = GetStr(row, "impactedValue"),
                        AnnualSavings  = savings,
                        Currency       = GetStr(row, "savingsCurrency")
                    });
                }
            }
            catch { graphFailed = true; }

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
                                var p  = item.TryGetProperty("properties", out var pp) ? pp : default;
                                var ext = p.TryGetProperty("extendedProperties", out var ep) ? ep : default;
                                string prob = GetPropStr(p, "shortDescription", "problem");
                                string sol  = GetPropStr(p, "shortDescription", "solution");

                                if (Regex.IsMatch(prob, "reserv|savings plan", RegexOptions.IgnoreCase))
                                    continue;

                                double? savings = ParseDouble(GetStr(ext, "annualSavingsAmount"))
                                               ?? ParseDouble(GetStr(ext, "savingsAmount"));

                                allRecs.Add(new OptimizationRecommendation
                                {
                                    Subscription   = sub.Name,
                                    SubscriptionId = sub.Id,
                                    Category       = Categorize(prob, sol),
                                    Impact         = GetStr(p, "impact"),
                                    Problem        = prob,
                                    Solution       = sol,
                                    ResourceType   = GetStr(p, "impactedField"),
                                    ResourceName   = GetStr(p, "impactedValue"),
                                    AnnualSavings  = savings,
                                    Currency       = GetStr(ext, "savingsCurrency")
                                });
                            }
                        }
                    }
                    catch { }
                }
            }

            // Build category/impact summaries
            var byCat    = new Dictionary<string, CategorySummary>(StringComparer.OrdinalIgnoreCase);
            var byImpact = new Dictionary<string, ImpactSummary>(StringComparer.OrdinalIgnoreCase);
            double totalSavings = 0;

            foreach (var rec in allRecs)
            {
                if (!byCat.TryGetValue(rec.Category, out var cs))
                {
                    cs = new CategorySummary { Category = rec.Category };
                    byCat[rec.Category] = cs;
                }
                cs.Count++;
                if (rec.AnnualSavings.HasValue)
                {
                    cs.TotalSavings += rec.AnnualSavings.Value;
                    totalSavings    += rec.AnnualSavings.Value;
                }

                if (!byImpact.TryGetValue(rec.Impact, out var imp))
                {
                    imp = new ImpactSummary { Impact = rec.Impact };
                    byImpact[rec.Impact] = imp;
                }
                imp.Count++;
            }

            return new OptimizationResult
            {
                Recommendations        = allRecs,
                ByCategory             = new List<CategorySummary>(byCat.Values),
                ByImpact               = new List<ImpactSummary>(byImpact.Values),
                TotalCount             = allRecs.Count,
                EstimatedAnnualSavings = Math.Round(totalSavings, 2)
            };
        }

        private static string Categorize(string problem, string solution)
        {
            string combined = $"{problem} {solution}";
            if (Regex.IsMatch(combined, @"right.?siz|resize|downsize|scale down|burstable|B-series", RegexOptions.IgnoreCase))
                return "Rightsize";
            if (Regex.IsMatch(combined, @"shut.?down|deallocate|idle|stopped", RegexOptions.IgnoreCase))
                return "Shutdown / Deallocate";
            if (Regex.IsMatch(combined, @"delet|unused|orphan|unattached", RegexOptions.IgnoreCase))
                return "Delete Unused";
            if (Regex.IsMatch(combined, @"modern|upgrade|migrate|move to", RegexOptions.IgnoreCase))
                return "Modernize";
            return "Other";
        }

        private static List<string> GetSubIds(IList<SubscriptionInfo> subs)
        {
            var ids = new List<string>(); foreach (var s in subs) ids.Add(s.Id); return ids;
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
