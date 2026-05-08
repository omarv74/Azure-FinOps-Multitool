using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Queries existing reservation and savings plan utilization to show how
    /// well current commitments are being used.
    /// Equivalent to Get-CommitmentUtilization.ps1.
    /// </summary>
    public class CommitmentService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public CommitmentService(AzureRestService rest) => _rest = rest;

        public async Task<CommitmentResult> GetCommitmentUtilizationAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Querying reservation utilization...");
            var commitments = new List<CommitmentItem>();

            // ── Reservations via subscription-independent endpoint ─────────────
            try
            {
                var doc = await _rest.GetAsync(
                    "/providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01", ct);

                if (doc?.RootElement.TryGetProperty("value", out var orders) == true)
                {
                    foreach (var order in orders.EnumerateArray())
                    {
                        string orderId = GetStr(order, "name");
                        var p = order.TryGetProperty("properties", out var pp) ? pp : default;

                        // Expand each reservation in the order
                        try
                        {
                            var resDoc = await _rest.GetAsync(
                                $"/providers/Microsoft.Capacity/reservationOrders/{orderId}/reservations?api-version=2022-11-01",
                                ct);

                            if (resDoc?.RootElement.TryGetProperty("value", out var reservations) == true)
                            {
                                foreach (var res in reservations.EnumerateArray())
                                {
                                    var rp = res.TryGetProperty("properties", out var rpp) ? rpp : default;
                                    double qty     = GetDouble(rp, "quantity");
                                    double usedQty = GetDouble(rp, "utilization.aggregate[0].value");
                                    if (usedQty == 0)
                                    {
                                        // Try alternate utilization path
                                        if (rp.TryGetProperty("utilization", out var util) &&
                                            util.TryGetProperty("aggregate", out var agg) &&
                                            agg.ValueKind == JsonValueKind.Array &&
                                            agg.GetArrayLength() > 0 &&
                                            agg[0].TryGetProperty("value", out var uval))
                                            usedQty = uval.ValueKind == JsonValueKind.Number ? uval.GetDouble() : 0;
                                    }

                                    double utilPct = qty > 0 ? Math.Round(usedQty / qty * 100, 1) : 0;

                                    commitments.Add(new CommitmentItem
                                    {
                                        Name           = GetStr(rp, "displayName"),
                                        Type           = "Reservation",
                                        SKU            = GetStr(rp, "skuName"),
                                        Scope          = GetStr(rp, "appliedScopeType"),
                                        Term           = GetStr(rp, "term"),
                                        Quantity       = qty,
                                        UsedQuantity   = usedQty,
                                        UtilizationPct = utilPct,
                                        ExpiryDate     = GetStr(rp, "expiryDate")
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Reservation query failed: {ex.Message}");
            }

            // ── Savings Plans ─────────────────────────────────────────────────
            StatusCallback?.Invoke("Querying savings plan utilization...");
            try
            {
                var doc = await _rest.GetAsync(
                    "/providers/Microsoft.BillingBenefits/savingsPlanOrders?api-version=2022-11-01", ct);

                if (doc?.RootElement.TryGetProperty("value", out var orders) == true)
                {
                    foreach (var order in orders.EnumerateArray())
                    {
                        string orderId = GetStr(order, "name");
                        try
                        {
                            var plansDoc = await _rest.GetAsync(
                                $"/providers/Microsoft.BillingBenefits/savingsPlanOrders/{orderId}/savingsPlans?api-version=2022-11-01",
                                ct);

                            if (plansDoc?.RootElement.TryGetProperty("value", out var plans) == true)
                            {
                                foreach (var plan in plans.EnumerateArray())
                                {
                                    var pp = plan.TryGetProperty("properties", out var ppp) ? ppp : default;
                                    double util = 0;
                                    if (pp.TryGetProperty("utilization", out var utilEl) &&
                                        utilEl.TryGetProperty("aggregates", out var agg) &&
                                        agg.ValueKind == JsonValueKind.Array &&
                                        agg.GetArrayLength() > 0 &&
                                        agg[0].TryGetProperty("value", out var uv))
                                        util = uv.ValueKind == JsonValueKind.Number ? uv.GetDouble() : 0;

                                    commitments.Add(new CommitmentItem
                                    {
                                        Name           = GetStr(pp, "displayName"),
                                        Type           = "Savings Plan",
                                        SKU            = GetStr(pp, "appliedScopeType"),
                                        Scope          = GetStr(pp, "appliedScopeType"),
                                        Term           = GetStr(pp, "term"),
                                        Quantity       = 1,
                                        UsedQuantity   = util / 100,
                                        UtilizationPct = Math.Round(util, 1),
                                        ExpiryDate     = GetStr(pp, "expiryDateTime")
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Savings plan query failed: {ex.Message}");
            }

            double avgUtil = 0;
            if (commitments.Count > 0)
            {
                double total = 0;
                foreach (var c in commitments) total += c.UtilizationPct;
                avgUtil = Math.Round(total / commitments.Count, 1);
            }

            return new CommitmentResult
            {
                Commitments    = commitments,
                TotalCount     = commitments.Count,
                AvgUtilization = avgUtil
            };
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        private static double GetDouble(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return 0;
        }
    }
}
