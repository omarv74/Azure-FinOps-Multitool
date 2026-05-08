using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Retrieves billing account structure (EA, MCA profiles, invoice sections).
    /// Equivalent to Get-BillingStructure.ps1.
    /// </summary>
    public class BillingService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public BillingService(AzureRestService rest) => _rest = rest;

        public async Task<BillingResult> GetBillingStructureAsync(CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Querying billing structure...");
            var result = new BillingResult();

            // ── Billing Accounts ──────────────────────────────────────────────
            try
            {
                var doc = await _rest.GetAsync(
                    "/providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01", ct);

                if (doc?.RootElement.TryGetProperty("value", out var accts) == true)
                {
                    result.HasBillingAccess = true;
                    foreach (var acct in accts.EnumerateArray())
                    {
                        var p = acct.TryGetProperty("properties", out var pp) ? pp : default;
                        result.BillingAccounts.Add(new BillingAccount
                        {
                            DisplayName   = GetStr(p, "displayName"),
                            AgreementType = GetStr(p, "agreementType"),
                            AccountType   = GetStr(p, "accountType"),
                            AccountStatus = GetStr(p, "accountStatus")
                        });

                        string acctName = acct.TryGetProperty("name", out var nm)
                            ? nm.GetString() ?? string.Empty : string.Empty;

                        // Query billing profiles for this account
                        await LoadBillingProfilesAsync(result, acctName, ct);

                        // For EA accounts, try departments
                        if (GetStr(p, "agreementType") == "EnterpriseAgreement")
                            await LoadEADepartmentsAsync(result, acctName, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Billing accounts query failed: {ex.Message}");
                result.HasBillingAccess = false;
            }

            // ── Cost Allocation Rules ─────────────────────────────────────────
            try
            {
                var doc = await _rest.GetAsync(
                    "/providers/Microsoft.CostManagement/costAllocationRules?api-version=2023-11-01", ct);
                if (doc?.RootElement.TryGetProperty("value", out var rules) == true)
                {
                    foreach (var rule in rules.EnumerateArray())
                    {
                        var name = rule.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                        if (name != null) result.CostAllocationRules.Add(name);
                    }
                }
            }
            catch { }

            return result;
        }

        private async Task LoadBillingProfilesAsync(
            BillingResult result, string accountName, CancellationToken ct)
        {
            try
            {
                var doc = await _rest.GetAsync(
                    $"/providers/Microsoft.Billing/billingAccounts/{accountName}/billingProfiles?api-version=2024-04-01",
                    ct);

                if (doc?.RootElement.TryGetProperty("value", out var profiles) == true)
                {
                    foreach (var profile in profiles.EnumerateArray())
                    {
                        var p = profile.TryGetProperty("properties", out var pp) ? pp : default;
                        string profileName = profile.TryGetProperty("name", out var nm)
                            ? nm.GetString() ?? string.Empty : string.Empty;

                        result.BillingProfiles.Add(new BillingProfile
                        {
                            DisplayName    = GetStr(p, "displayName"),
                            BillingAccount = accountName,
                            Currency       = GetStr(p, "currency"),
                            InvoiceDay     = p.TryGetProperty("invoiceDay", out var id) && id.ValueKind == JsonValueKind.Number
                                ? id.GetInt32() : 0,
                            Status         = GetStr(p, "status")
                        });

                        await LoadInvoiceSectionsAsync(result, accountName, profileName, ct);
                    }
                }
            }
            catch { }
        }

        private async Task LoadInvoiceSectionsAsync(
            BillingResult result, string accountName, string profileName, CancellationToken ct)
        {
            try
            {
                var doc = await _rest.GetAsync(
                    $"/providers/Microsoft.Billing/billingAccounts/{accountName}/billingProfiles/{profileName}" +
                    "/invoiceSections?api-version=2024-04-01", ct);

                if (doc?.RootElement.TryGetProperty("value", out var sections) == true)
                {
                    foreach (var section in sections.EnumerateArray())
                    {
                        var p = section.TryGetProperty("properties", out var pp) ? pp : default;
                        result.InvoiceSections.Add(new InvoiceSection
                        {
                            DisplayName    = GetStr(p, "displayName"),
                            BillingProfile = profileName,
                            BillingAccount = accountName,
                            State          = GetStr(p, "state")
                        });
                    }
                }
            }
            catch { }
        }

        private async Task LoadEADepartmentsAsync(
            BillingResult result, string accountName, CancellationToken ct)
        {
            try
            {
                var doc = await _rest.GetAsync(
                    $"/providers/Microsoft.Billing/billingAccounts/{accountName}/departments?api-version=2019-10-01-preview",
                    ct);

                if (doc?.RootElement.TryGetProperty("value", out var depts) == true)
                {
                    foreach (var dept in depts.EnumerateArray())
                    {
                        var p = dept.TryGetProperty("properties", out var pp) ? pp : default;
                        var name = GetStr(p, "departmentName");
                        if (!string.IsNullOrEmpty(name))
                            result.EADepartments.Add(name);
                    }
                }
            }
            catch { }
        }

        private static string GetStr(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) ? v.GetString() ?? string.Empty : string.Empty;
    }
}
