using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Queries Cost Management per-resource to get individual resource costs.
    /// Tries MG-scope first, falls back to per-subscription.
    /// Equivalent to Get-ResourceCosts.ps1.
    /// </summary>
    public class ResourceCostService
    {
        private readonly AzureRestService _rest;
        private bool _mgScopeFailed;
        public Action<string>? StatusCallback { get; set; }

        private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft.compute/virtualmachines"]          = "Virtual Machine",
            ["microsoft.compute/disks"]                    = "Managed Disk",
            ["microsoft.network/loadbalancers"]            = "Load Balancer",
            ["microsoft.network/applicationgateways"]      = "App Gateway",
            ["microsoft.network/azurefirewalls"]           = "Azure Firewall",
            ["microsoft.network/publicipaddresses"]        = "Public IP",
            ["microsoft.network/virtualnetworkgateways"]   = "VNet Gateway",
            ["microsoft.containerservice/managedclusters"] = "AKS Cluster",
            ["microsoft.sql/servers/databases"]            = "SQL Database",
            ["microsoft.storage/storageaccounts"]          = "Storage Account",
            ["microsoft.web/sites"]                        = "App Service",
            ["microsoft.web/serverfarms"]                  = "App Service Plan",
            ["microsoft.keyvault/vaults"]                  = "Key Vault",
            ["microsoft.operationalinsights/workspaces"]   = "Log Analytics",
            ["microsoft.insights/components"]              = "App Insights",
            ["microsoft.recoveryservices/vaults"]          = "Recovery Vault",
            ["microsoft.dbformysql/flexibleservers"]       = "MySQL Flexible",
            ["microsoft.dbforpostgresql/flexibleservers"]  = "PostgreSQL Flexible",
            ["microsoft.cosmosdb/databaseaccounts"]        = "Cosmos DB",
            ["microsoft.cache/redis"]                      = "Redis Cache",
            ["microsoft.cdn/profiles"]                     = "CDN / Front Door",
            ["microsoft.containerregistry/registries"]     = "Container Registry",
            ["microsoft.apimanagement/service"]            = "API Management",
            ["microsoft.servicebus/namespaces"]            = "Service Bus",
        };

        public ResourceCostService(AzureRestService rest) => _rest = rest;

        public async Task<List<ResourceCostItem>> GetResourceCostsAsync(
            string tenantId,
            IList<SubscriptionInfo> subs,
            CancellationToken ct = default)
        {
            var results = new List<ResourceCostItem>();

            if (!_mgScopeFailed && !string.IsNullOrEmpty(tenantId))
            {
                StatusCallback?.Invoke("Querying resource costs (MG scope)...");
                try
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
                                new { type = "Dimension", name = "ResourceId" },
                                new { type = "Dimension", name = "ResourceGroupName" }
                            }
                        }
                    });

                    var mgPath = $"/providers/Microsoft.Management/managementGroups/{tenantId}" +
                                 "/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                    var doc = await _rest.PostAsync(mgPath, body, ct);
                    if (doc == null) throw new InvalidOperationException("No data from MG-scope resource cost query.");

                    results = ParseResourceCostRows(doc.RootElement);
                    return results;
                }
                catch (Exception ex)
                {
                    StatusCallback?.Invoke($"MG resource costs failed: {ex.Message} – per-sub fallback.");
                    _mgScopeFailed = true;
                }
            }

            // Per-subscription fallback
            int i = 0;
            foreach (var sub in subs)
            {
                i++;
                if (i % Math.Max(1, subs.Count / 10) == 0 || i == 1 || i == subs.Count)
                    StatusCallback?.Invoke($"Querying resource costs ({i}/{subs.Count} subs)...");

                ct.ThrowIfCancellationRequested();
                try
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
                                new { type = "Dimension", name = "ResourceId" },
                                new { type = "Dimension", name = "ResourceGroupName" }
                            }
                        }
                    });

                    var path = $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                    var doc = await _rest.PostAsync(path, body, ct);
                    if (doc != null)
                    {
                        var subItems = ParseResourceCostRows(doc.RootElement);
                        foreach (var item in subItems)
                        {
                            if (string.IsNullOrEmpty(item.SubscriptionId))
                                item.SubscriptionId = sub.Id;
                        }
                        results.AddRange(subItems);
                    }
                }
                catch (Exception ex)
                {
                    StatusCallback?.Invoke($"Resource cost failed for {sub.Name}: {ex.Message}");
                }
            }

            return results;
        }

        private static List<ResourceCostItem> ParseResourceCostRows(JsonElement root)
        {
            var items = new List<ResourceCostItem>();
            if (!root.TryGetProperty("properties", out var props)) return items;
            if (!props.TryGetProperty("rows", out var rows)) return items;

            // Determine column indices
            int costIdx = 0, resourceIdIdx = 1, rgIdx = 2, currIdx = 3, subIdx = -1;
            if (props.TryGetProperty("columns", out var cols))
            {
                for (int ci = 0; ci < cols.GetArrayLength(); ci++)
                {
                    var colName = cols[ci].TryGetProperty("name", out var nm) ? nm.GetString() : string.Empty;
                    var colNameLower = colName?.ToLowerInvariant() ?? string.Empty;
                    if (colNameLower.Contains("cost"))  costIdx = ci;
                    else if (colNameLower == "resourceid") resourceIdIdx = ci;
                    else if (colNameLower is "resourcegroupname" or "resourcegroup") rgIdx = ci;
                    else if (colNameLower.Contains("currency")) currIdx = ci;
                    else if (colNameLower == "subscriptionid") subIdx = ci;
                }
            }

            foreach (var row in rows.EnumerateArray())
            {
                int len = row.GetArrayLength();
                if (len == 0) continue;

                double cost = row[costIdx].ValueKind == JsonValueKind.Number
                    ? Math.Round(row[costIdx].GetDouble(), 2) : 0;
                if (cost <= 0) continue;

                string resourceId = resourceIdIdx < len ? row[resourceIdIdx].GetString() ?? string.Empty : string.Empty;
                string rg         = rgIdx < len         ? row[rgIdx].GetString() ?? string.Empty : string.Empty;
                string currency   = currIdx < len        ? row[currIdx].GetString() ?? "USD" : "USD";
                string subId      = subIdx >= 0 && subIdx < len ? row[subIdx].GetString() ?? string.Empty : string.Empty;

                // Infer resource type from resource ID path
                string resourceType = InferTypeFromId(resourceId);

                items.Add(new ResourceCostItem
                {
                    ResourcePath   = resourceId,
                    ResourceGroup  = rg,
                    ResourceType   = resourceType,
                    Actual         = cost,
                    Forecast       = cost,
                    Currency       = currency,
                    SubscriptionId = subId
                });
            }

            return items;
        }

        private static string InferTypeFromId(string resourceId)
        {
            if (string.IsNullOrEmpty(resourceId)) return "Unknown";

            // Extract "providers/X/Y" from the resource ID
            var lower = resourceId.ToLowerInvariant();
            foreach (var kvp in TypeMap)
            {
                if (lower.Contains(kvp.Key))
                    return kvp.Value;
            }

            // Fallback: extract the type from the path segments
            var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int provIdx = Array.FindIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
            if (provIdx >= 0 && provIdx + 2 < parts.Length)
                return $"{parts[provIdx + 1]}/{parts[provIdx + 2]}";

            return "Unknown";
        }
    }
}
