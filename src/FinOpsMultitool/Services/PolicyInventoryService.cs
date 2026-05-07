using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Retrieves all policy assignments and compliance state per subscription.
    /// Equivalent to Get-PolicyInventory.ps1.
    /// </summary>
    public class PolicyInventoryService
    {
        private readonly AzureRestService _rest;
        private readonly ResourceGraphService _graph;
        public Action<string>? StatusCallback { get; set; }

        public PolicyInventoryService(AzureRestService rest, ResourceGraphService graph)
        {
            _rest  = rest;
            _graph = graph;
        }

        public async Task<PolicyInventoryResult> GetPolicyInventoryAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Querying policy assignments via Resource Graph...");

            var assignments = new List<PolicyAssignment>();
            var subIds = GetSubIds(subs);
            var subNames = BuildSubNameMap(subs);

            // ── Policy assignments via Resource Graph ─────────────────────────
            var query = @"
policyresources
| where type == 'microsoft.authorization/policyassignments'
| project assignmentId = id,
    assignmentName = name,
    policyDefId    = tostring(properties.policyDefinitionId),
    effect         = tostring(properties.parameters.effect.value),
    enforcementMode = tostring(properties.enforcementMode),
    displayName    = tostring(properties.displayName),
    subscriptionId,
    scope          = tostring(properties.scope)";

            bool graphFailed = false;
            try
            {
                var rows = await _graph.QueryAsync(query, subIds, ct: ct);
                foreach (var row in rows)
                {
                    string subId = GetStr(row, "subscriptionId");
                    assignments.Add(new PolicyAssignment
                    {
                        AssignmentName  = GetStr(row, "assignmentName"),
                        AssignmentId    = GetStr(row, "assignmentId"),
                        PolicyDefId     = GetStr(row, "policyDefId"),
                        Effect          = GetStr(row, "effect"),
                        EnforcementMode = GetStr(row, "enforcementMode"),
                        Origin          = "ResourceGraph",
                        Subscription    = subNames.GetValueOrDefault(subId, subId),
                        Scope           = GetStr(row, "scope")
                    });
                }
            }
            catch { graphFailed = true; }

            // ── Per-subscription REST fallback ────────────────────────────────
            if (graphFailed)
            {
                StatusCallback?.Invoke("Falling back to per-subscription policy REST...");
                foreach (var sub in subs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var doc = await _rest.GetAsync(
                            $"/subscriptions/{sub.Id}/providers/Microsoft.Authorization" +
                            "/policyAssignments?api-version=2022-06-01", ct);

                        if (doc?.RootElement.TryGetProperty("value", out var val) == true)
                        {
                            foreach (var item in val.EnumerateArray())
                            {
                                var p = item.TryGetProperty("properties", out var pp) ? pp : default;
                                string effect = string.Empty;
                                if (p.TryGetProperty("parameters", out var parms) &&
                                    parms.TryGetProperty("effect", out var effEl) &&
                                    effEl.TryGetProperty("value", out var ev))
                                    effect = ev.GetString() ?? string.Empty;

                                assignments.Add(new PolicyAssignment
                                {
                                    AssignmentName  = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty,
                                    AssignmentId    = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                                    PolicyDefId     = GetStr(p, "policyDefinitionId"),
                                    Effect          = effect,
                                    EnforcementMode = GetStr(p, "enforcementMode"),
                                    Origin          = "REST",
                                    Subscription    = sub.Name,
                                    Scope           = GetStr(p, "scope")
                                });
                            }
                        }
                    }
                    catch { }
                }
            }

            // ── Compliance state per subscription ─────────────────────────────
            StatusCallback?.Invoke("Querying policy compliance state...");
            var complianceMap = new Dictionary<string, PolicyComplianceEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var doc = await _rest.PostAsync(
                        $"/subscriptions/{sub.Id}/providers/Microsoft.PolicyInsights" +
                        "/policyStates/latest/summarize?api-version=2019-10-01",
                        "{}", ct);

                    if (doc?.RootElement.TryGetProperty("value", out var val) == true &&
                        val.GetArrayLength() > 0)
                    {
                        var summary = val[0];
                        int nonCompliant = 0;
                        if (summary.TryGetProperty("results", out var results))
                        {
                            if (results.TryGetProperty("queryResultsUri", out _))
                            {
                                // Simplified: just count non-compliant from policyAssignments
                            }
                            nonCompliant = results.TryGetProperty("nonCompliantResources", out var nc)
                                ? nc.GetInt32() : 0;
                        }

                        complianceMap[sub.Id] = new PolicyComplianceEntry
                        {
                            Subscription   = sub.Name,
                            NonCompliant   = nonCompliant,
                            Compliant      = 0,
                            TotalResources = nonCompliant
                        };
                    }
                }
                catch { }
            }

            int totalNonCompliant = 0;
            foreach (var entry in complianceMap.Values)
                totalNonCompliant += entry.NonCompliant;

            return new PolicyInventoryResult
            {
                Assignments         = assignments,
                ComplianceBySubMap  = complianceMap,
                TotalAssignments    = assignments.Count,
                TotalNonCompliant   = totalNonCompliant
            };
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
    }
}
