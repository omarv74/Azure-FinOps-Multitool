using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Uses Resource Graph to find orphaned/idle resources consuming costs
    /// without serving active workloads.
    /// Equivalent to Get-OrphanedResources.ps1.
    /// </summary>
    public class OrphanedResourcesService
    {
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public OrphanedResourcesService(ResourceGraphService graph) => _graph = graph;

        public async Task<OrphanedResourcesResult> GetOrphanedResourcesAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Scanning for orphaned and idle resources...");
            var subIds  = GetSubIds(subs);
            var orphans = new List<OrphanedResource>();

            // ── 1: Orphaned Managed Disks ─────────────────────────────────────
            await RunQueryAsync(orphans, subIds, @"
resources
| where type =~ 'microsoft.compute/disks'
| where managedBy == '' or isnull(managedBy)
| where properties.diskState == 'Unattached'
| project name, resourceGroup, subscriptionId, location,
          diskSizeGb = properties.diskSizeGB,
          sku = sku.name",
                row => new OrphanedResource
                {
                    Category       = "Orphaned Disk",
                    ResourceName   = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    Detail         = $"{GetStr(row, "diskSizeGb")} GB ({GetStr(row, "sku")})",
                    Impact         = "Medium"
                }, "Scanning orphaned disks...", ct);

            // ── 2: Unattached Public IPs ──────────────────────────────────────
            await RunQueryAsync(orphans, subIds, @"
resources
| where type =~ 'microsoft.network/publicipaddresses'
| where properties.ipConfiguration == '' or isnull(properties.ipConfiguration)
| where properties.natGateway == '' or isnull(properties.natGateway)
| project name, resourceGroup, subscriptionId, location,
          sku = sku.name, allocationMethod = properties.publicIPAllocationMethod",
                row => new OrphanedResource
                {
                    Category       = "Unattached Public IP",
                    ResourceName   = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    Detail         = $"{GetStr(row, "sku")} - {GetStr(row, "allocationMethod")}",
                    Impact         = GetStr(row, "sku") == "Standard" ? "Medium" : "Low"
                }, "Scanning unattached public IPs...", ct);

            // ── 3: Unattached NICs ────────────────────────────────────────────
            await RunQueryAsync(orphans, subIds, @"
resources
| where type =~ 'microsoft.network/networkinterfaces'
| where isnull(properties.virtualMachine) or properties.virtualMachine == ''
| where isnull(properties.privateEndpoint) or properties.privateEndpoint == ''
| project name, resourceGroup, subscriptionId, location,
          accelerated = properties.enableAcceleratedNetworking",
                row => new OrphanedResource
                {
                    Category       = "Unattached NIC",
                    ResourceName   = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    Detail         = $"Accelerated: {GetStr(row, "accelerated")}",
                    Impact         = "Low"
                }, "Scanning unattached NICs...", ct);

            // ── 4: Deallocated VMs ────────────────────────────────────────────
            await RunQueryAsync(orphans, subIds, @"
resources
| where type =~ 'microsoft.compute/virtualmachines'
| where properties.extended.instanceView.powerState.displayStatus == 'VM deallocated'
    or properties.extended.instanceView.powerState.code == 'PowerState/deallocated'
| project name, resourceGroup, subscriptionId, location,
          vmSize = properties.hardwareProfile.vmSize",
                row => new OrphanedResource
                {
                    Category       = "Deallocated VM",
                    ResourceName   = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    Detail         = $"{GetStr(row, "vmSize")} - still incurs disk/IP costs",
                    Impact         = "Medium"
                }, "Scanning deallocated VMs...", ct);

            // ── 5: Empty App Service Plans ────────────────────────────────────
            await RunQueryAsync(orphans, subIds, @"
resources
| where type =~ 'microsoft.web/serverfarms'
| where properties.numberOfSites == 0
| where sku.tier != 'Free' and sku.tier != 'Shared'
| project name, resourceGroup, subscriptionId, location,
          sku = strcat(sku.tier, ' / ', sku.name),
          workers = properties.numberOfWorkers",
                row => new OrphanedResource
                {
                    Category       = "Empty App Service Plan",
                    ResourceName   = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    Detail         = $"{GetStr(row, "sku")}, {GetStr(row, "workers")} worker(s), 0 apps",
                    Impact         = "High"
                }, "Scanning empty App Service Plans...", ct);

            // ── 6: Old Snapshots (30d+) ───────────────────────────────────────
            string cutoff = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            await RunQueryAsync(orphans, subIds, $@"
resources
| where type =~ 'microsoft.compute/snapshots'
| where properties.timeCreated < datetime('{cutoff}')
| project name, resourceGroup, subscriptionId, location,
          diskSizeGb = properties.diskSizeGB,
          timeCreated = properties.timeCreated",
                row => new OrphanedResource
                {
                    Category       = "Old Snapshot (30d+)",
                    ResourceName   = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    Detail         = $"{GetStr(row, "diskSizeGb")} GB, created {GetStr(row, "timeCreated")}",
                    Impact         = "Low"
                }, "Scanning old snapshots...", ct);

            // Build summary
            var summaryMap = new Dictionary<string, OrphanCategorySummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in orphans)
            {
                if (!summaryMap.TryGetValue(o.Category, out var s))
                {
                    s = new OrphanCategorySummary { Category = o.Category };
                    summaryMap[o.Category] = s;
                }
                s.Count++;
            }

            return new OrphanedResourcesResult
            {
                Orphans    = orphans,
                Summary    = new List<OrphanCategorySummary>(summaryMap.Values),
                TotalCount = orphans.Count,
                HasData    = orphans.Count > 0
            };
        }

        private async Task RunQueryAsync(
            List<OrphanedResource> orphans,
            List<string> subIds,
            string query,
            Func<JsonElement, OrphanedResource> mapper,
            string label,
            CancellationToken ct)
        {
            StatusCallback?.Invoke(label);
            try
            {
                var rows = await _graph.QueryAsync(query, subIds, ct: ct);
                foreach (var row in rows)
                    orphans.Add(mapper(row));
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Query failed ({label}): {ex.Message}");
            }
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
