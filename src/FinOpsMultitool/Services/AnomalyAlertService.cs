using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Retrieves cost anomaly alert rules and triggered alerts.
    /// Equivalent to Get-AnomalyAlerts.ps1.
    /// </summary>
    public class AnomalyAlertService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public AnomalyAlertService(AzureRestService rest) => _rest = rest;

        public async Task<AnomalyAlertsResult> GetAnomalyAlertsAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Querying cost anomaly alerts...");

            var triggered   = new List<AnomalyAlert>();
            var configured  = new List<AnomalyAlert>();

            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Alert rules (configured)
                    var rulesDoc = await _rest.GetAsync(
                        $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement" +
                        "/scheduledActions?api-version=2023-11-01",
                        ct);

                    if (rulesDoc?.RootElement.TryGetProperty("value", out var rules) == true)
                    {
                        foreach (var rule in rules.EnumerateArray())
                        {
                            var p = rule.TryGetProperty("properties", out var pp) ? pp : default;
                            string kind = GetStr(rule, "kind");
                            // Only include anomaly alert rules
                            if (!kind.Contains("Anomaly", StringComparison.OrdinalIgnoreCase) &&
                                !GetStr(p, "viewId").Contains("anomaly", StringComparison.OrdinalIgnoreCase))
                                continue;

                            configured.Add(new AnomalyAlert
                            {
                                Subscription  = sub.Name,
                                AlertName     = GetStr(rule, "name"),
                                AlertType     = kind,
                                Status        = GetStr(p, "status"),
                                TimeModified  = GetStr(p, "lastRunTime"),
                                Description   = GetStr(p, "displayName"),
                                Source        = "ScheduledAction"
                            });
                        }
                    }
                }
                catch { }

                try
                {
                    // Triggered alerts
                    var alertsDoc = await _rest.GetAsync(
                        $"/subscriptions/{sub.Id}/providers/Microsoft.CostManagement" +
                        "/alerts?api-version=2023-11-01",
                        ct);

                    if (alertsDoc?.RootElement.TryGetProperty("value", out var alerts) == true)
                    {
                        foreach (var alert in alerts.EnumerateArray())
                        {
                            var p = alert.TryGetProperty("properties", out var pp) ? pp : default;
                            string typeStr = GetStr(p, "definition.type");
                            if (string.IsNullOrEmpty(typeStr))
                            {
                                // Try nested path
                                if (p.TryGetProperty("definition", out var def))
                                    typeStr = GetStr(def, "type");
                            }

                            triggered.Add(new AnomalyAlert
                            {
                                Subscription  = sub.Name,
                                AlertName     = GetStr(alert, "name"),
                                AlertType     = typeStr,
                                Status        = GetStr(p, "status"),
                                TimeModified  = GetStr(p, "timeModified"),
                                Description   = GetStr(p, "description"),
                                Source        = "CostManagementAlert"
                            });
                        }
                    }
                }
                catch { }
            }

            return new AnomalyAlertsResult
            {
                TriggeredAlerts  = triggered,
                ConfiguredRules  = configured,
                HasData          = triggered.Count > 0 || configured.Count > 0
            };
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;
    }
}
