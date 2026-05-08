using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Compares existing tags against a curated list of recommended FinOps
    /// tags and returns gap analysis.
    /// Equivalent to Get-TagRecommendations.ps1.
    /// </summary>
    public class TagRecommendationsService
    {
        public Action<string>? StatusCallback { get; set; }

        // Curated list of recommended FinOps tags
        private static readonly List<(string Tag, string Priority, string Pillar, string Purpose)> RecommendedTags = new()
        {
            ("Environment",   "Required",    "Understand",  "Identify Production vs Dev/Test/Staging workloads for cost allocation"),
            ("CostCenter",    "Required",    "Allocate",    "Map costs to financial cost center for chargeback and showback"),
            ("Owner",         "Required",    "Govern",      "Identify the person or team responsible for the resource cost"),
            ("Project",       "Required",    "Allocate",    "Attribute costs to a specific project or initiative"),
            ("Application",   "Recommended", "Understand",  "Group costs by application for application-level cost visibility"),
            ("Department",    "Recommended", "Allocate",    "Attribute costs to business department for departmental showback"),
            ("BusinessUnit",  "Recommended", "Allocate",    "Top-level business unit alignment for executive cost reporting"),
            ("CreatedBy",     "Recommended", "Govern",      "Track who provisioned the resource for governance and accountability"),
            ("CreatedDate",   "Optional",    "Govern",      "Track resource creation date for lifecycle management"),
            ("ExpiryDate",    "Optional",    "Optimize",    "Mark resources with planned expiry to enable automated cleanup"),
            ("ManagedBy",     "Optional",    "Govern",      "Indicate if managed by Terraform, Bicep, or another IaC tool"),
            ("Workload",      "Optional",    "Understand",  "Identify the workload type (e.g., batch, real-time, analytics)"),
            ("Criticality",   "Optional",    "Govern",      "Classify resource criticality for prioritized cost optimization"),
            ("DataClass",     "Optional",    "Govern",      "Data classification for compliance (Public, Internal, Confidential)"),
        };

        public Task<TagRecsResult> GetTagRecommendationsAsync(
            TagInventoryResult tags, CancellationToken ct = default)
        {
            StatusCallback?.Invoke("Analyzing tag recommendations...");

            var existingTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tags?.TagNames != null)
            {
                foreach (var tag in tags.TagNames.Keys)
                    existingTags.Add(tag);
            }

            var analysis = new List<TagRecItem>();
            int presentCount = 0;

            foreach (var (tag, priority, pillar, purpose) in RecommendedTags)
            {
                ct.ThrowIfCancellationRequested();

                bool isPresent = existingTags.Contains(tag);
                if (isPresent) presentCount++;

                // Determine how many resources have this tag
                string location = string.Empty;
                if (isPresent && tags?.TagLocations?.TryGetValue(tag, out var locs) == true && locs.Count > 0)
                {
                    location = locs.Count > 3
                        ? $"{string.Join(", ", locs.GetRange(0, 3))} +{locs.Count - 3} more"
                        : string.Join(", ", locs);
                }

                string status = isPresent ? "Present" : "Missing";
                string actionLabel = isPresent ? "In Use" : priority == "Required" ? "Add Required Tag" : "Add Recommended Tag";
                string actionTagName = isPresent ? string.Empty : tag;

                analysis.Add(new TagRecItem
                {
                    Tag         = tag,
                    Status      = status,
                    Location    = location,
                    Priority    = priority,
                    Pillar      = pillar,
                    Purpose     = purpose,
                    ActionLabel = actionLabel,
                    ActionTagName = actionTagName
                });
            }

            double compliancePct = RecommendedTags.Count > 0
                ? Math.Round((double)presentCount / RecommendedTags.Count * 100, 1) : 0;

            return Task.FromResult(new TagRecsResult
            {
                Analysis          = analysis,
                Present           = presentCount,
                CompliancePercent = compliancePct
            });
        }
    }
}
