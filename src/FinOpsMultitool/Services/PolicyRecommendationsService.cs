using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Compares existing policy assignments against a curated list of
    /// FinOps-recommended policies and returns gap analysis.
    /// Equivalent to Get-PolicyRecommendations.ps1.
    /// </summary>
    public class PolicyRecommendationsService
    {
        public Action<string>? StatusCallback { get; set; }

        private static readonly List<PolicyRecItem> RecommendedPolicies = new()
        {
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/726aca4c-86e9-4b04-b0c5-073027359532",
                DisplayName   = "Require a tag on resources",
                Category      = "Tags",
                Pillar        = "Understand",
                Priority      = "Required",
                DefaultEffect = "Deny",
                AllowedEffects = new List<string> { "Audit", "Deny", "Disabled" },
                Purpose       = "Enforce tagging on all resources for cost allocation and chargeback visibility",
                Parameters    = new List<PolicyParam>
                {
                    new() { Name = "tagName",  Label = "Tag name (e.g. CostCenter)", Required = true },
                    new() { Name = "tagValue", Label = "Tag value (leave blank for any value)", Required = false }
                }
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/96670d01-0a4d-4649-9c89-2d3abc0a5025",
                DisplayName   = "Require a tag on resource groups",
                Category      = "Tags",
                Pillar        = "Understand",
                Priority      = "Required",
                DefaultEffect = "Deny",
                AllowedEffects = new List<string> { "Audit", "Deny", "Disabled" },
                Purpose       = "Enforce tagging on resource groups for cost allocation at the container level",
                Parameters    = new List<PolicyParam>
                {
                    new() { Name = "tagName", Label = "Tag name (e.g. CostCenter)", Required = true }
                }
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/ea3f2387-9b95-492a-a190-fcbef5-37f7",
                DisplayName   = "Inherit a tag from the resource group if missing",
                Category      = "Tags",
                Pillar        = "Understand",
                Priority      = "Recommended",
                DefaultEffect = "Modify",
                AllowedEffects = new List<string> { "Modify", "Disabled" },
                Purpose       = "Auto-inherit tags from resource group to child resources",
                Parameters    = new List<PolicyParam>
                {
                    new() { Name = "tagName", Label = "Tag name to inherit (e.g. CostCenter)", Required = true }
                }
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/40df99da-1232-49b1-a39a-6da8d878f469",
                DisplayName   = "Inherit a tag from the subscription if missing",
                Category      = "Tags",
                Pillar        = "Understand",
                Priority      = "Recommended",
                DefaultEffect = "Modify",
                AllowedEffects = new List<string> { "Modify", "Disabled" },
                Purpose       = "Auto-inherit subscription-level tags to resource groups and resources",
                Parameters    = new List<PolicyParam>
                {
                    new() { Name = "tagName", Label = "Tag name to inherit", Required = true }
                }
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/0a914e76-4921-4c19-b460-a2d36003525a",
                DisplayName   = "Allowed locations",
                Category      = "General",
                Pillar        = "Govern",
                Priority      = "Recommended",
                DefaultEffect = "Deny",
                AllowedEffects = new List<string> { "Deny", "Disabled" },
                Purpose       = "Restrict resources to approved Azure regions to control data residency and pricing",
                Parameters    = new List<PolicyParam>
                {
                    new() { Name = "listOfAllowedLocations", Label = "Allowed regions (e.g. eastus, westeurope)", Required = true }
                }
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/cccc23c7-8427-4f53-ad12-b6a63eb452b3",
                DisplayName   = "Allowed virtual machine SKUs",
                Category      = "Compute",
                Pillar        = "Optimize",
                Priority      = "Optional",
                DefaultEffect = "Deny",
                AllowedEffects = new List<string> { "Audit", "Deny", "Disabled" },
                Purpose       = "Restrict VM sizes to prevent accidental provisioning of expensive SKUs",
                Parameters    = new List<PolicyParam>
                {
                    new() { Name = "listOfAllowedSKUs", Label = "Allowed VM SKUs", Required = true }
                }
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/7433c107-6db4-4ad1-b57a-a76dce0154a1",
                DisplayName   = "Storage accounts should have infrastructure encryption",
                Category      = "Storage",
                Pillar        = "Govern",
                Priority      = "Optional",
                DefaultEffect = "Audit",
                AllowedEffects = new List<string> { "Audit", "Deny", "Disabled" },
                Purpose       = "Ensure storage accounts use encryption for compliance",
                Parameters    = new List<PolicyParam>()
            },
            new PolicyRecItem
            {
                PolicyDefId   = "/providers/Microsoft.Authorization/policyDefinitions/2b9ad585-36bc-4615-b300-fd4435808332",
                DisplayName   = "Azure Defender for servers should be enabled",
                Category      = "Security Center",
                Pillar        = "Govern",
                Priority      = "Recommended",
                DefaultEffect = "AuditIfNotExists",
                AllowedEffects = new List<string> { "AuditIfNotExists", "Disabled" },
                Purpose       = "Enable Microsoft Defender for Servers for enhanced security monitoring",
                Parameters    = new List<PolicyParam>()
            }
        };

        public Task<PolicyRecsResult> GetPolicyRecommendationsAsync(
            PolicyInventoryResult inventory, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Analyzing policy recommendations...");

            // Build set of assigned policy def IDs
            var assignedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in inventory.Assignments)
            {
                if (!string.IsNullOrEmpty(a.PolicyDefId))
                    assignedIds.Add(a.PolicyDefId);
            }

            var analysis = new List<PolicyRecItem>();
            int assignedCount = 0;

            for (int i = 0; i < RecommendedPolicies.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rec = RecommendedPolicies[i];
                bool isAssigned = assignedIds.Contains(rec.PolicyDefId);

                if (isAssigned) assignedCount++;

                analysis.Add(new PolicyRecItem
                {
                    PolicyDefId    = rec.PolicyDefId,
                    DisplayName    = rec.DisplayName,
                    Policy         = rec.DisplayName,
                    Status         = isAssigned ? "Assigned" : "Not Assigned",
                    Category       = rec.Category,
                    Priority       = rec.Priority,
                    Pillar         = rec.Pillar,
                    Purpose        = rec.Purpose,
                    Effect         = isAssigned ? GetAssignedEffect(inventory, rec.PolicyDefId) : string.Empty,
                    DefaultEffect  = rec.DefaultEffect,
                    AllowedEffects = rec.AllowedEffects,
                    Parameters     = rec.Parameters,
                    PolicyIndex    = i
                });
            }

            double compliancePct = RecommendedPolicies.Count > 0
                ? Math.Round((double)assignedCount / RecommendedPolicies.Count * 100, 1) : 0;

            return Task.FromResult(new PolicyRecsResult
            {
                Analysis      = analysis,
                Assigned      = assignedCount,
                CompliancePct = compliancePct
            });
        }

        private static string GetAssignedEffect(PolicyInventoryResult inventory, string policyDefId)
        {
            foreach (var a in inventory.Assignments)
            {
                if (a.PolicyDefId.Equals(policyDefId, StringComparison.OrdinalIgnoreCase))
                    return a.Effect;
            }
            return string.Empty;
        }
    }
}
