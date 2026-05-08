using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Finds running VMs with low CPU + network utilization over 14 days.
    /// Queries Azure Monitor metrics via REST.
    /// Equivalent to Get-IdleVMs.ps1.
    /// </summary>
    public class IdleVMService
    {
        private readonly AzureRestService _rest;
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        private const double CpuThreshold     = 5.0;   // < 5% avg CPU = idle
        private const double NetworkThreshold14d = 1048576 * 14; // < 1 MB/day for 14 days

        public IdleVMService(AzureRestService rest, ResourceGraphService graph)
        {
            _rest  = rest;
            _graph = graph;
        }

        public async Task<IdleVMResult> GetIdleVMsAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Scanning for idle and underutilized VMs...");
            var subIds = GetSubIds(subs);

            // ── 1: Find all running VMs ───────────────────────────────────────
            var vmQuery = @"
resources
| where type =~ 'microsoft.compute/virtualmachines'
| extend powerState = tostring(properties.extended.instanceView.powerState.code)
| where powerState =~ 'PowerState/running'
| project name, resourceGroup, subscriptionId, location,
          vmSize = properties.hardwareProfile.vmSize,
          osType = properties.storageProfile.osDisk.osType,
          powerState";

            var runningVMs = await _graph.QuerySafeAsync(vmQuery, subIds, "Querying running VMs...", ct);
            StatusCallback?.Invoke($"Running VMs found: {runningVMs.Count}. Checking metrics...");

            if (runningVMs.Count == 0)
            {
                return new IdleVMResult { IdleVMs = new List<IdleVM>(), Count = 0, HasData = false, ScannedVMs = 0 };
            }

            // ── 2: Query 14-day metrics per VM ────────────────────────────────
            string token = await _rest.GetBearerTokenAsync(ct);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var now           = DateTime.UtcNow;
            var fourteenDaysAgo = now.AddDays(-14).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nowStr          = now.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var results = new List<IdleVM>();
            int vmIdx = 0;

            foreach (var vm in runningVMs)
            {
                vmIdx++;
                ct.ThrowIfCancellationRequested();

                if (vmIdx % Math.Max(1, runningVMs.Count / 10) == 0 || vmIdx == 1)
                    StatusCallback?.Invoke($"Checking VM metrics ({vmIdx}/{runningVMs.Count})...");

                string name    = GetStr(vm, "name");
                string rg      = GetStr(vm, "resourceGroup");
                string subId   = GetStr(vm, "subscriptionId");
                string location = GetStr(vm, "location");
                string vmSize  = GetStr(vm, "vmSize");
                string os      = GetStr(vm, "osType");

                string scope = $"/subscriptions/{subId}/resourceGroups/{rg}" +
                               $"/providers/Microsoft.Compute/virtualMachines/{name}";

                try
                {
                    var metricUri = $"{_rest.BaseUrl}{scope}/providers/Microsoft.Insights/metrics" +
                                    $"?api-version=2023-10-01" +
                                    $"&metricnames=Percentage CPU,Network In Total,Network Out Total" +
                                    $"&timespan={fourteenDaysAgo}/{nowStr}" +
                                    $"&aggregation=Average,Total&interval=P14D";

                    var resp = await http.GetAsync(metricUri, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var content = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(content);

                    double? avgCpu = null;
                    double totalNetIn = 0, totalNetOut = 0;

                    if (doc.RootElement.TryGetProperty("value", out var metrics))
                    {
                        foreach (var metric in metrics.EnumerateArray())
                        {
                            string metricName = metric.TryGetProperty("name", out var mn) &&
                                                mn.TryGetProperty("value", out var mv)
                                ? mv.GetString() ?? string.Empty : string.Empty;

                            if (!metric.TryGetProperty("timeseries", out var ts)) continue;
                            foreach (var series in ts.EnumerateArray())
                            {
                                if (!series.TryGetProperty("data", out var data)) continue;
                                foreach (var dp in data.EnumerateArray())
                                {
                                    switch (metricName)
                                    {
                                        case "Percentage CPU":
                                            if (dp.TryGetProperty("average", out var avg) &&
                                                avg.ValueKind == JsonValueKind.Number)
                                                avgCpu = avg.GetDouble();
                                            break;
                                        case "Network In Total":
                                            if (dp.TryGetProperty("total", out var ni) &&
                                                ni.ValueKind == JsonValueKind.Number)
                                                totalNetIn += ni.GetDouble();
                                            break;
                                        case "Network Out Total":
                                            if (dp.TryGetProperty("total", out var no) &&
                                                no.ValueKind == JsonValueKind.Number)
                                                totalNetOut += no.GetDouble();
                                            break;
                                    }
                                }
                            }
                        }
                    }

                    double totalNetwork = totalNetIn + totalNetOut;
                    string? classification = null;

                    if (avgCpu.HasValue && avgCpu < CpuThreshold && totalNetwork < NetworkThreshold14d)
                        classification = "Idle";
                    else if (avgCpu.HasValue && avgCpu < 10 && totalNetwork < NetworkThreshold14d * 10)
                        classification = "Underutilized";

                    if (classification != null)
                    {
                        double dailyNetMB = Math.Round(totalNetwork / 14 / 1_048_576, 2);
                        results.Add(new IdleVM
                        {
                            VMName         = name,
                            ResourceGroup  = rg,
                            SubscriptionId = subId,
                            Location       = location,
                            VMSize         = vmSize,
                            OS             = os,
                            AvgCPU14d      = avgCpu.HasValue ? Math.Round(avgCpu.Value, 1) : 0,
                            NetworkPerDay  = $"{dailyNetMB} MB",
                            Classification = classification,
                            Recommendation = classification == "Idle" ? "Deallocate or delete" : "Downsize VM"
                        });
                    }
                }
                catch { }
            }

            StatusCallback?.Invoke($"Idle/underutilized VMs: {results.Count}");

            return new IdleVMResult
            {
                IdleVMs    = results,
                Count      = results.Count,
                HasData    = results.Count > 0,
                ScannedVMs = runningVMs.Count
            };
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
