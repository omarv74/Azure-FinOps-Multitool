using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Uses Resource Graph to discover every tag name/value in use across
    /// all subscriptions, plus untagged resource counts and details.
    /// Equivalent to Get-TagInventory.ps1.
    /// </summary>
    public class TagInventoryService
    {
        private readonly AzureRestService _rest;
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public TagInventoryService(AzureRestService rest, ResourceGraphService graph)
        {
            _rest  = rest;
            _graph = graph;
        }

        public async Task<TagInventoryResult> GetTagInventoryAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            var subIds = GetSubIds(subs);
            var subNameMap = BuildSubNameMap(subs);

            // ── Query 1: Tag names, values, and counts ────────────────────────
            StatusCallback?.Invoke("Scanning tag inventory via Resource Graph...");
            var tagQuery = @"
resources
| union resourcecontainers
| mvexpand tags
| extend tagName = tostring(bag_keys(tags)[0])
| extend tagValue = tostring(tags[tagName])
| where isnotempty(tagName)
| summarize ResourceCount = count(), ResourceTypes = make_set(type) by tagName, tagValue
| order by tagName asc, ResourceCount desc";

            var tagRows = await _graph.QuerySafeAsync(tagQuery, subIds, null, ct);

            var tagNames = new Dictionary<string, TagEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in tagRows)
            {
                string name  = GetStr(row, "tagName");
                string value = GetStr(row, "tagValue");
                int count    = GetInt(row, "ResourceCount");
                var types    = GetStringArray(row, "ResourceTypes");

                if (!tagNames.TryGetValue(name, out var entry))
                {
                    entry = new TagEntry();
                    tagNames[name] = entry;
                }
                entry.TotalResources += count;
                entry.Values.Add(new TagValue
                {
                    Value         = value,
                    ResourceCount = count,
                    ResourceTypes = types
                });
            }

            // ── Query 2: Untagged resource count ──────────────────────────────
            StatusCallback?.Invoke("Counting untagged resources...");
            int untaggedCount = 0;
            try
            {
                var countBody = JsonSerializer.Serialize(new
                {
                    subscriptions = subIds,
                    query = "resources | where isnull(tags) or tags == '{}' | summarize UntaggedCount = count()"
                });
                var countDoc = await _rest.PostAsync(
                    "/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
                    countBody, ct);
                if (countDoc?.RootElement.TryGetProperty("data", out var dataEl) == true &&
                    dataEl.GetArrayLength() > 0 &&
                    dataEl[0].TryGetProperty("UntaggedCount", out var uc))
                    untaggedCount = uc.GetInt32();
            }
            catch { }

            // ── Query 3: Total resource count ─────────────────────────────────
            int totalCount = 0;
            try
            {
                var totalBody = JsonSerializer.Serialize(new
                {
                    subscriptions = subIds,
                    query = "resources | summarize TotalCount = count()"
                });
                var totalDoc = await _rest.PostAsync(
                    "/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01",
                    totalBody, ct);
                if (totalDoc?.RootElement.TryGetProperty("data", out var dataEl) == true &&
                    dataEl.GetArrayLength() > 0 &&
                    dataEl[0].TryGetProperty("TotalCount", out var tc))
                    totalCount = tc.GetInt32();
            }
            catch { }

            // ── Query 4: Untagged resource details (top 500) ──────────────────
            var untaggedResources = new List<UntaggedResource>();
            var untaggedQuery = @"
resources
| where isnull(tags) or tags == '{}'
| project name, type, resourceGroup, subscriptionId, location
| order by type asc, name asc
| take 500";

            var udRows = await _graph.QuerySafeAsync(untaggedQuery, subIds, null, ct);
            foreach (var row in udRows)
            {
                string subId = GetStr(row, "subscriptionId");
                untaggedResources.Add(new UntaggedResource
                {
                    ResourceName  = GetStr(row, "name"),
                    ResourceType  = GetStr(row, "type"),
                    ResourceGroup = GetStr(row, "resourceGroup"),
                    Subscription  = subNameMap.GetValueOrDefault(subId, subId),
                    Location      = GetStr(row, "location")
                });
            }

            // ── Query 5: Tag locations ────────────────────────────────────────
            var tagLocations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var locQuery = @"
resources
| union resourcecontainers
| mvexpand tags
| extend tagName = tostring(bag_keys(tags)[0])
| where isnotempty(tagName)
| summarize ResourceCount = count() by tagName, subscriptionId, resourceGroup
| order by tagName asc, ResourceCount desc";

            var locRows = await _graph.QuerySafeAsync(locQuery, subIds, null, ct);
            foreach (var row in locRows)
            {
                string tName = GetStr(row, "tagName");
                string subId = GetStr(row, "subscriptionId");
                string rg    = GetStr(row, "resourceGroup");
                string subName = subNameMap.GetValueOrDefault(subId, subId);
                string loc = $"{subName} / {rg}";

                if (!tagLocations.TryGetValue(tName, out var locs))
                {
                    locs = new List<string>();
                    tagLocations[tName] = locs;
                }
                if (!locs.Contains(loc))
                    locs.Add(loc);
            }

            // Fallback untagged count
            if (untaggedCount == 0 && untaggedResources.Count > 0)
                untaggedCount = untaggedResources.Count;

            int taggedCount = totalCount - untaggedCount;
            double tagCoverage = totalCount > 0
                ? Math.Round((double)taggedCount / totalCount * 100, 1) : 0;

            return new TagInventoryResult
            {
                TagNames          = tagNames,
                TagCount          = tagNames.Count,
                TagLocations      = tagLocations,
                TotalResources    = totalCount,
                TaggedCount       = taggedCount,
                UntaggedCount     = untaggedCount,
                TagCoverage       = tagCoverage,
                UntaggedResources = untaggedResources
            };
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

        private static int GetInt(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
                if (int.TryParse(v.GetString(), out var i)) return i;
            }
            return 0;
        }

        private static List<string> GetStringArray(JsonElement el, string prop)
        {
            var list = new List<string>();
            if (!el.TryGetProperty(prop, out var v)) return list;
            if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    var s = item.GetString();
                    if (s != null) list.Add(s);
                }
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (s != null) list.Add(s);
            }
            return list;
        }
    }
}
