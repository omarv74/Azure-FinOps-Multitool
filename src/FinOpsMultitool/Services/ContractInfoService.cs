using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Detects the customer's Azure contract type (EA, MCA, PAYGO, CSP)
    /// from billing accounts API and subscription quotaId.
    /// Equivalent to Get-ContractInfo.ps1.
    /// </summary>
    public class ContractInfoService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public ContractInfoService(AzureRestService rest) => _rest = rest;

        public async Task<List<ContractInfo>> GetContractInfoAsync(
            IList<SubscriptionInfo> subs,
            CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Detecting Azure contract type...");

            string? inferredAgreement = null;
            string? inferredFriendly  = null;

            // Step 1: Detect agreement type from subscription quotaId
            int checksToRun = Math.Min(3, subs.Count);
            for (int i = 0; i < checksToRun && inferredAgreement == null; i++)
            {
                try
                {
                    var doc = await _rest.GetAsync(
                        $"/subscriptions/{subs[i].Id}?api-version=2022-12-01", ct);
                    if (doc == null) continue;

                    string quotaId = string.Empty;
                    if (doc.RootElement.TryGetProperty("properties", out var props) &&
                        props.TryGetProperty("subscriptionPolicies", out var pol) &&
                        pol.TryGetProperty("quotaId", out var qel))
                        quotaId = qel.GetString() ?? string.Empty;

                    if (string.IsNullOrEmpty(quotaId)) continue;

                    (inferredAgreement, inferredFriendly) = MapQuotaId(quotaId);
                }
                catch { }
            }

            // Step 2: Try billing accounts API
            try
            {
                var doc = await _rest.GetAsync(
                    "/providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01", ct);

                if (doc?.RootElement.TryGetProperty("value", out var val) == true &&
                    val.GetArrayLength() > 0)
                {
                    JsonElement matchedAccount = default;
                    bool found = false;

                    // If inferred and multiple accounts, try to match
                    if (inferredAgreement != null && val.GetArrayLength() > 1)
                    {
                        foreach (var acct in val.EnumerateArray())
                        {
                            var agr = acct.TryGetProperty("properties", out var p) &&
                                      p.TryGetProperty("agreementType", out var a)
                                      ? a.GetString() : null;
                            if (agr == inferredAgreement)
                            {
                                matchedAccount = acct;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                        matchedAccount = val[0];

                    if (matchedAccount.ValueKind != JsonValueKind.Undefined)
                    {
                        var name = matchedAccount.TryGetProperty("name", out var nm)
                            ? nm.GetString() : string.Empty;
                        var p2 = matchedAccount.TryGetProperty("properties", out var pp) ? pp : default;
                        var agreementType = p2.ValueKind != JsonValueKind.Undefined &&
                                            p2.TryGetProperty("agreementType", out var at)
                                            ? at.GetString() ?? string.Empty : string.Empty;
                        var displayName = p2.ValueKind != JsonValueKind.Undefined &&
                                          p2.TryGetProperty("displayName", out var dn)
                                          ? dn.GetString() ?? string.Empty : string.Empty;
                        var status = p2.ValueKind != JsonValueKind.Undefined &&
                                     p2.TryGetProperty("accountStatus", out var st)
                                     ? st.GetString() ?? string.Empty : string.Empty;

                        return new List<ContractInfo>
                        {
                            new ContractInfo
                            {
                                AccountName   = displayName,
                                AccountId     = name ?? string.Empty,
                                AgreementType = agreementType,
                                FriendlyType  = ToFriendly(agreementType),
                                AccountStatus = status,
                                Currency      = "Unknown"
                            }
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                StatusCallback?.Invoke($"Billing account query failed: {ex.Message}");
            }

            // Step 3: Return quotaId-based inference
            if (inferredAgreement != null)
            {
                var subName = subs.Count > 0 ? subs[0].Name : "Unknown";
                return new List<ContractInfo>
                {
                    new ContractInfo
                    {
                        AccountName   = $"Inferred from subscription: {subName}",
                        AccountId     = subs.Count > 0 ? subs[0].Id : string.Empty,
                        AgreementType = inferredAgreement,
                        FriendlyType  = inferredFriendly ?? inferredAgreement,
                        AccountStatus = "Active",
                        Currency      = "Unknown"
                    }
                };
            }

            return new List<ContractInfo>
            {
                new ContractInfo
                {
                    AccountName   = "Unknown",
                    AgreementType = "Unknown",
                    FriendlyType  = "Could not detect (assign Billing Reader for accurate detection)"
                }
            };
        }

        private static (string agreement, string friendly) MapQuotaId(string quotaId)
        {
            if (Regex.IsMatch(quotaId, "EnterpriseAgreement", RegexOptions.IgnoreCase))
                return ("EnterpriseAgreement", "Enterprise Agreement (EA)");
            if (Regex.IsMatch(quotaId, "MCSFree|MSDN|Visual", RegexOptions.IgnoreCase))
                return ("MSDN", "Visual Studio / MSDN");
            if (Regex.IsMatch(quotaId, "PayAsYouGo|PAYG|MSAZR", RegexOptions.IgnoreCase))
                return ("MicrosoftOnlineServicesProgram", "Pay-As-You-Go (PAYGO)");
            if (Regex.IsMatch(quotaId, "Sponsored", RegexOptions.IgnoreCase))
                return ("Sponsored", "Azure Sponsored");
            if (Regex.IsMatch(quotaId, "CSP", RegexOptions.IgnoreCase))
                return ("MicrosoftPartnerAgreement", "CSP / Partner Agreement");
            if (Regex.IsMatch(quotaId, "Internal", RegexOptions.IgnoreCase))
                return ("Internal", "Microsoft Internal");
            if (Regex.IsMatch(quotaId, "MCA", RegexOptions.IgnoreCase))
                return ("MicrosoftCustomerAgreement", "Microsoft Customer Agreement (MCA)");
            if (Regex.IsMatch(quotaId, "FreeTrial", RegexOptions.IgnoreCase))
                return ("FreeTrial", "Free Trial");
            return (quotaId, quotaId);
        }

        private static string ToFriendly(string agreementType) => agreementType switch
        {
            "EnterpriseAgreement"           => "Enterprise Agreement (EA)",
            "MicrosoftCustomerAgreement"    => "Microsoft Customer Agreement (MCA)",
            "MicrosoftOnlineServicesProgram" => "Pay-As-You-Go (PAYGO)",
            "MicrosoftPartnerAgreement"     => "CSP / Partner Agreement (MPA)",
            _ => agreementType
        };
    }
}
