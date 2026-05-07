using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Handles Azure authentication and tenant/subscription discovery.
    /// Equivalent to Initialize-Scanner.ps1.
    /// Returns a TenantInfo with all accessible subscriptions.
    /// </summary>
    public class AuthService
    {
        private readonly AzureRestService _rest;

        public Action<string>? StatusCallback { get; set; }

        public AuthService(AzureRestService rest)
        {
            _rest = rest;
        }

        /// <summary>
        /// Authenticates, lists tenants, shows picker dialog, loads subscriptions.
        /// On WPF apps, parentWindow should be the main Window.
        /// </summary>
        public async Task<TenantInfo?> ConnectAsync(
            string environment = "AzureCloud",
            CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Authenticating to Azure...");

            // List accessible subscriptions via ARM
            var subsDoc = await _rest.GetAsync(
                "/subscriptions?api-version=2022-12-01", ct);

            if (subsDoc == null)
            {
                StatusCallback?.Invoke("Failed to list subscriptions.");
                return null;
            }

            var subs = new List<SubscriptionInfo>();
            if (subsDoc.RootElement.TryGetProperty("value", out var subsArr))
            {
                foreach (var subEl in subsArr.EnumerateArray())
                {
                    var id = subEl.GetString("subscriptionId") ?? string.Empty;
                    var name = subEl.GetString("displayName") ?? id;
                    var state = subEl.GetString("state") ?? "Enabled";
                    subs.Add(new SubscriptionInfo { Id = id, Name = name, State = state });
                }
            }

            // Determine tenantId from first subscription
            string tenantId = string.Empty;
            if (subs.Count > 0)
            {
                var firstSubDoc = await _rest.GetAsync(
                    $"/subscriptions/{subs[0].Id}?api-version=2022-12-01", ct);
                if (firstSubDoc?.RootElement.TryGetProperty("tenantId", out var tidEl) == true)
                    tenantId = tidEl.GetString() ?? string.Empty;
            }

            // Retrieve account name from token (use /tenants endpoint)
            string accountName = "Unknown";
            try
            {
                var tenantsDoc = await _rest.GetAsync("/tenants?api-version=2022-12-01", ct);
                if (tenantsDoc?.RootElement.TryGetProperty("value", out var tenArr) == true
                    && tenArr.GetArrayLength() > 0)
                {
                    var first = tenArr[0];
                    if (tenantId == string.Empty &&
                        first.TryGetProperty("tenantId", out var t))
                        tenantId = t.GetString() ?? string.Empty;
                }
            }
            catch { }

            // Filter: skip VS/MSDN/DevTest/Free subscriptions
            var skipPatterns = new[]
            {
                "Visual Studio", "MSDN", "Free Trial", "Sponsorship",
                "Access to Azure Active Directory", "Azure Pass",
                "BizSpark", "Imagine", "MPN", "Azure in Open"
            };

            var prodSubs = new List<SubscriptionInfo>();
            var skippedSubs = new List<SubscriptionInfo>();

            foreach (var sub in subs)
            {
                bool skip = false;
                foreach (var pattern in skipPatterns)
                {
                    if (sub.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    { skip = true; break; }
                }

                if (skip || sub.State != "Enabled")
                    skippedSubs.Add(sub);
                else
                    prodSubs.Add(sub);
            }

            // Classify tenant size
            string tenantSize = prodSubs.Count <= 10 ? "Small"
                              : prodSubs.Count <= 50 ? "Medium"
                              : "Large";

            StatusCallback?.Invoke(
                $"Found {prodSubs.Count} production subscriptions ({tenantSize} tenant).");

            return new TenantInfo
            {
                TenantId      = tenantId,
                AccountName   = accountName,
                Subscriptions = prodSubs,
                SkippedSubs   = skippedSubs,
                Environment   = environment,
                TenantSize    = tenantSize
            };
        }
    }

    internal static class JsonElementExtensions
    {
        internal static string? GetString(this JsonElement el, string property)
        {
            if (el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
    }
}
