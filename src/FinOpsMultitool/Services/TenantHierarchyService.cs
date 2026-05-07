using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Retrieves management group hierarchy (with subscription mapping).
    /// Equivalent to Get-TenantHierarchy.ps1.
    /// Falls back to a flat subscription list if the API is unavailable.
    /// </summary>
    public class TenantHierarchyService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public TenantHierarchyService(AzureRestService rest) => _rest = rest;

        public async Task<HierarchyResult> GetHierarchyAsync(
            string tenantId,
            IList<SubscriptionInfo> subs,
            CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Loading management group hierarchy...");

            try
            {
                // GET root management group with children expanded (depth = recurse)
                var doc = await _rest.GetAsync(
                    $"/providers/Microsoft.Management/managementGroups/{tenantId}" +
                    "?api-version=2020-05-01&$expand=children&$recurse=true",
                    ct);

                if (doc == null)
                    return BuildFallback(tenantId, subs);

                var root = ParseManagementGroup(doc.RootElement);
                var subMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                BuildSubMap(root, subMap);

                return new HierarchyResult
                {
                    RootGroup       = root,
                    SubscriptionMap = subMap,
                    FlatSubs        = new List<SubscriptionInfo>(subs)
                };
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Hierarchy failed: {ex.Message} – using flat list.");
                return BuildFallback(tenantId, subs);
            }
        }

        private static ManagementGroup ParseManagementGroup(JsonElement el)
        {
            var mg = new ManagementGroup();

            if (el.TryGetProperty("properties", out var props))
            {
                if (props.TryGetProperty("displayName", out var dn))
                    mg.DisplayName = dn.GetString() ?? string.Empty;
            }
            if (el.TryGetProperty("name", out var nm))
                mg.Name = nm.GetString() ?? string.Empty;
            if (el.TryGetProperty("type", out var tp))
                mg.Type = tp.GetString() ?? string.Empty;

            // Parse children
            if (el.TryGetProperty("properties", out var p2) &&
                p2.TryGetProperty("children", out var children) &&
                children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    var childMg = ParseManagementGroup(child);
                    // Subscriptions have type /subscriptions
                    if (child.TryGetProperty("type", out var t) &&
                        t.GetString()?.Contains("subscriptions") == true)
                    {
                        childMg.Type = "/subscriptions";
                    }
                    mg.Children.Add(childMg);
                }
            }

            return mg;
        }

        private static void BuildSubMap(ManagementGroup group, Dictionary<string, string> map)
        {
            foreach (var child in group.Children)
            {
                if (child.Type == "/subscriptions")
                {
                    map[child.Name] = group.DisplayName;
                }
                else
                {
                    BuildSubMap(child, map);
                }
            }
        }

        private static HierarchyResult BuildFallback(string tenantId, IList<SubscriptionInfo> subs)
        {
            return new HierarchyResult
            {
                RootGroup = new ManagementGroup
                {
                    DisplayName = "Tenant Root",
                    Name = tenantId
                },
                SubscriptionMap = new Dictionary<string, string>(),
                FlatSubs = new List<SubscriptionInfo>(subs)
            };
        }
    }
}
