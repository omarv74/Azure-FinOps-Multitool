using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Queries Cost Management to get costs grouped by tag.
    /// Equivalent to Get-CostByTag.ps1.
    /// </summary>
    public class CostByTagService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public CostByTagService(AzureRestService rest) => _rest = rest;

        public async Task<CostByTagResult> GetCostByTagAsync(
            string tenantId,
            IList<SubscriptionInfo> subs,
            TagInventoryResult? tagInventory = null,
            CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Querying costs by tag...");

            var result = new CostByTagResult();

            // Pick the top tag names to query (up to 5 most used)
            List<string> tagsToQuery;
            if (tagInventory?.TagNames?.Count > 0)
            {
                tagsToQuery = tagInventory.TagNames
                    .OrderByDescending(kv => kv.Value.TotalResources)
                    .Take(5)
                    .Select(kv => kv.Key)
                    .ToList();
            }
            else
            {
                tagsToQuery = new List<string> { "Environment", "CostCenter", "Owner", "Project", "Application" };
            }

            result.TagsQueried = tagsToQuery;

            if (tagsToQuery.Count == 0)
            {
                result.NoTagsFound = true;
                return result;
            }

            foreach (var tagName in tagsToQuery)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    double totalCost = await QueryCostByTagAsync(tenantId, subs, tagName, ct);
                    if (totalCost > 0)
                        result.CostByTag[tagName] = Math.Round(totalCost, 2);
                }
                catch (Exception ex)
                {
                    StatusCallback?.Invoke($"Cost-by-tag failed for '{tagName}': {ex.Message}");
                }
            }

            result.NoTagsFound = result.CostByTag.Count == 0;
            return result;
        }

        private async Task<double> QueryCostByTagAsync(
            string tenantId, IList<SubscriptionInfo> subs, string tagName, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                type = "ActualCost",
                timeframe = "MonthToDate",
                dataset = new
                {
                    granularity = "None",
                    aggregation = new
                    {
                        totalCost = new { name = "Cost", function = "Sum" }
                    },
                    grouping = new[]
                    {
                        new { type = "TagKey", name = tagName }
                    },
                    filter = new
                    {
                        tags = new
                        {
                            name = tagName,
                            @operator = "Exists"
                        }
                    }
                }
            });

            // Try MG scope
            if (!string.IsNullOrEmpty(tenantId))
            {
                var path = $"/providers/Microsoft.Management/managementGroups/{tenantId}" +
                           "/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                var doc = await _rest.PostAsync(path, body, ct);
                if (doc != null)
                    return SumCostRows(doc.RootElement);
            }

            // Per-sub fallback
            double total = 0;
            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var path = $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                    var doc = await _rest.PostAsync(path, body, ct);
                    if (doc != null) total += SumCostRows(doc.RootElement);
                }
                catch { }
            }
            return total;
        }

        private static double SumCostRows(JsonElement root)
        {
            double total = 0;
            if (!root.TryGetProperty("properties", out var props)) return total;
            if (!props.TryGetProperty("rows", out var rows)) return total;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.GetArrayLength() > 0 && row[0].ValueKind == JsonValueKind.Number)
                    total += row[0].GetDouble();
            }
            return total;
        }
    }
}
