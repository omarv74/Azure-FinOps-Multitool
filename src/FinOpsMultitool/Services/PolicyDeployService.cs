using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Creates and removes policy assignments, creates remediation tasks.
    /// Equivalent to Deploy-PolicyAssignment.ps1.
    /// </summary>
    public class PolicyDeployService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public PolicyDeployService(AzureRestService rest) => _rest = rest;

        public async Task<(bool Success, string Message)> DeployPolicyAsync(
            string scope,
            string policyDefinitionId,
            string effect,
            string displayName,
            Dictionary<string, string>? parameters = null,
            CancellationToken ct = default)
        {
            if (!scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) &&
                !scope.StartsWith("/providers/Microsoft.Management/", StringComparison.OrdinalIgnoreCase))
                return (false, "Invalid scope format.");

            string[] validEffects = { "Audit", "Deny", "Disabled", "AuditIfNotExists",
                                      "DeployIfNotExists", "Modify", "Append" };
            bool validEffect = Array.Exists(validEffects,
                e => e.Equals(effect, StringComparison.OrdinalIgnoreCase));
            if (!validEffect)
                return (false, $"Invalid effect: {effect}");

            StatusCallback?.Invoke($"Deploying policy '{displayName}' to {scope}...");

            // Generate assignment name
            string defGuid   = policyDefinitionId.Split('/')[^1];
            string scopeHash = ComputeShortHash(scope);
            string assignName = $"finops-{scopeHash}-{defGuid}";
            if (assignName.Length > 128) assignName = assignName[..128];

            string assignDisplayName = !string.IsNullOrEmpty(displayName)
                ? $"FinOps: {displayName}"
                : "FinOps Policy Assignment";

            // Query the policy definition to discover valid parameter names
            var validParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var defDoc = await _rest.GetAsync(
                    $"{policyDefinitionId}?api-version=2021-06-01", ct);
                if (defDoc?.RootElement.TryGetProperty("properties", out var defProps) == true &&
                    defProps.TryGetProperty("parameters", out var defParams) &&
                    defParams.ValueKind == JsonValueKind.Object)
                {
                    foreach (var param in defParams.EnumerateObject())
                        validParamNames.Add(param.Name);
                }
            }
            catch { }

            // Build policy parameters
            var policyParams = new Dictionary<string, object>();
            if (validParamNames.Count == 0 || validParamNames.Contains("effect"))
                policyParams["effect"] = new { value = effect };

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    if (validParamNames.Count == 0 || validParamNames.Contains(kv.Key))
                        policyParams[kv.Key] = new { value = kv.Value };
                }
            }

            var body = JsonSerializer.Serialize(new
            {
                properties = new
                {
                    displayName        = assignDisplayName,
                    description        = "Deployed by Azure FinOps Multitool",
                    policyDefinitionId,
                    parameters         = policyParams,
                    enforcementMode    = "Default"
                }
            });

            var assignPath = $"{scope}/providers/Microsoft.Authorization/policyAssignments/{assignName}?api-version=2022-06-01";
            var doc = await _rest.PutAsync(assignPath, body, ct);

            return doc != null
                ? (true, $"Policy '{assignDisplayName}' assigned to {scope}")
                : (false, "Policy assignment failed – check permissions.");
        }

        public async Task<(bool Success, string Message)> RemovePolicyAssignmentAsync(
            string assignmentId, CancellationToken ct = default)
        {
            StatusCallback?.Invoke($"Removing policy assignment {assignmentId}...");
            var success = await _rest.DeleteAsync(
                $"{assignmentId}?api-version=2022-06-01", ct);
            return success
                ? (true, "Policy assignment removed.")
                : (false, "Failed to remove policy assignment.");
        }

        public async Task<(bool Success, string Message)> CreateRemediationAsync(
            string scope, string policyAssignmentId, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Creating remediation task...");
            string remName = $"finops-remediation-{ComputeShortHash(policyAssignmentId)}";

            var body = JsonSerializer.Serialize(new
            {
                properties = new
                {
                    policyAssignmentId,
                    resourceDiscoveryMode = "ReEvaluateCompliance"
                }
            });

            var path = $"{scope}/providers/Microsoft.PolicyInsights/remediations/{remName}?api-version=2021-10-01";
            var doc = await _rest.PutAsync(path, body, ct);

            return doc != null
                ? (true, "Remediation task created.")
                : (false, "Remediation task creation failed.");
        }

        public async Task<List<(string DisplayName, string ResourceId)>> GetPolicyScopesAsync(
            IList<SubscriptionInfo> subs, CancellationToken ct = default)
        {
            var scopes = new List<(string, string)>();

            foreach (var sub in subs)
            {
                scopes.Add(($"Subscription: {sub.Name}", $"/subscriptions/{sub.Id}"));
                ct.ThrowIfCancellationRequested();
                try
                {
                    var doc = await _rest.GetAsync(
                        $"/subscriptions/{sub.Id}/resourceGroups?api-version=2022-09-01", ct);
                    if (doc?.RootElement.TryGetProperty("value", out var val) == true)
                    {
                        foreach (var rg in val.EnumerateArray())
                        {
                            string name = rg.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                            string id   = rg.TryGetProperty("id", out var rid) ? rid.GetString() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrEmpty(name))
                                scopes.Add(($"  RG: {sub.Name}/{name}", id));
                        }
                    }
                }
                catch { }
            }

            return scopes;
        }

        private static string ComputeShortHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "")[..8].ToLower();
        }
    }
}
