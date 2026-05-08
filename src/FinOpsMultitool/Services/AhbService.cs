using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Finds VMs and SQL resources not using Azure Hybrid Benefit.
    /// Equivalent to Get-AHBOpportunities.ps1.
    /// </summary>
    public class AhbService
    {
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public AhbService(ResourceGraphService graph) => _graph = graph;

        public async Task<AhbResult> GetAhbOpportunitiesAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            var subIds = GetSubIds(subs);

            // ── Windows VMs without AHB ───────────────────────────────────────
            StatusCallback?.Invoke("Scanning Windows VMs for AHB eligibility...");
            var windowsVMs = new List<AhbItem>();
            var vmQuery = @"
resources
| where type == 'microsoft.compute/virtualmachines'
| where properties.storageProfile.osDisk.osType =~ 'Windows'
| where isempty(properties.licenseType) or properties.licenseType !~ 'Windows_Server'
| project name, resourceGroup, subscriptionId, location,
          vmSize = properties.hardwareProfile.vmSize,
          currentLicense = coalesce(tostring(properties.licenseType), 'None'),
          osType = tostring(properties.storageProfile.imageReference.offer)
| order by subscriptionId asc, name asc";

            var vmRows = await _graph.QuerySafeAsync(vmQuery, subIds, null, ct);
            foreach (var row in vmRows)
            {
                windowsVMs.Add(new AhbItem
                {
                    Name           = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    VmSize         = GetStr(row, "vmSize"),
                    CurrentLicense = GetStr(row, "currentLicense"),
                    OsType         = GetStr(row, "osType")
                });
            }

            // ── SQL Server VMs without AHB ────────────────────────────────────
            StatusCallback?.Invoke("Scanning SQL Server VMs for AHB eligibility...");
            var sqlVMs = new List<AhbItem>();
            var sqlVMQuery = @"
resources
| where type == 'microsoft.sqlvirtualmachine/sqlvirtualmachines'
| where isempty(properties.sqlServerLicenseType) or properties.sqlServerLicenseType !~ 'AHUB'
| project name, resourceGroup, subscriptionId, location,
          currentLicense = coalesce(tostring(properties.sqlServerLicenseType), 'None'),
          sqlEdition = tostring(properties.sqlImageSku)
| order by subscriptionId asc, name asc";

            var sqlVMRows = await _graph.QuerySafeAsync(sqlVMQuery, subIds, null, ct);
            foreach (var row in sqlVMRows)
            {
                sqlVMs.Add(new AhbItem
                {
                    Name           = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    CurrentLicense = GetStr(row, "currentLicense"),
                    SqlEdition     = GetStr(row, "sqlEdition")
                });
            }

            // ── SQL Databases without AHB ─────────────────────────────────────
            StatusCallback?.Invoke("Scanning SQL Databases for AHB eligibility...");
            var sqlDBs = new List<AhbItem>();
            var sqlDBQuery = @"
resources
| where type == 'microsoft.sql/servers/databases'
| where sku.tier != 'Free' and name != 'master'
| where isempty(properties.licenseType) or properties.licenseType !~ 'BasePrice'
| project name, resourceGroup, subscriptionId, location,
          currentLicense = coalesce(tostring(properties.licenseType), 'LicenseIncluded'),
          sku = strcat(tostring(sku.tier), ' / ', tostring(sku.name)),
          maxSizeGB = tolong(properties.maxSizeBytes) / 1073741824
| order by subscriptionId asc, name asc";

            var sqlDBRows = await _graph.QuerySafeAsync(sqlDBQuery, subIds, null, ct);
            foreach (var row in sqlDBRows)
            {
                double? sz = null;
                if (row.TryGetProperty("maxSizeGB", out var szEl) && szEl.ValueKind == JsonValueKind.Number)
                    sz = szEl.GetDouble();

                sqlDBs.Add(new AhbItem
                {
                    Name           = GetStr(row, "name"),
                    ResourceGroup  = GetStr(row, "resourceGroup"),
                    SubscriptionId = GetStr(row, "subscriptionId"),
                    Location       = GetStr(row, "location"),
                    CurrentLicense = GetStr(row, "currentLicense"),
                    Sku            = GetStr(row, "sku"),
                    MaxSizeGb      = sz
                });
            }

            int total = windowsVMs.Count + sqlVMs.Count + sqlDBs.Count;
            return new AhbResult
            {
                WindowsVMs         = windowsVMs,
                SqlVMs             = sqlVMs,
                SqlDatabases       = sqlDBs,
                TotalOpportunities = total,
                Summary            = $"Found {windowsVMs.Count} Windows VMs, {sqlVMs.Count} SQL VMs, {sqlDBs.Count} SQL DBs eligible for AHB"
            };
        }

        private static List<string> GetSubIds(IList<SubscriptionInfo> subs)
        {
            var ids = new List<string>();
            foreach (var s in subs) ids.Add(s.Id);
            return ids;
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;
    }
}
