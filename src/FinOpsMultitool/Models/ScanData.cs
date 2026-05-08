using System.Collections.Generic;

namespace FinOpsMultitool.Models
{
    // ── Authentication / Tenant ─────────────────────────────────────────────

    public class TenantInfo
    {
        public string TenantId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public List<SubscriptionInfo> Subscriptions { get; set; } = new();
        public string Environment { get; set; } = "AzureCloud";
        public string TenantSize { get; set; } = "Small";
        public List<SubscriptionInfo> SkippedSubs { get; set; } = new();
    }

    public class SubscriptionInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "Enabled";
    }

    // ── Hierarchy ────────────────────────────────────────────────────────────

    public class ManagementGroup
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public List<ManagementGroup> Children { get; set; } = new();
    }

    public class HierarchyResult
    {
        public ManagementGroup? RootGroup { get; set; }
        public Dictionary<string, string> SubscriptionMap { get; set; } = new();
        public List<SubscriptionInfo> FlatSubs { get; set; } = new();
    }

    // ── Contract / Billing ───────────────────────────────────────────────────

    public class ContractInfo
    {
        public string AccountName { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string AgreementType { get; set; } = string.Empty;
        public string FriendlyType { get; set; } = string.Empty;
        public string AccountStatus { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
    }

    // ── Cost Data ────────────────────────────────────────────────────────────

    public class SubscriptionCost
    {
        public double Actual { get; set; }
        public double Forecast { get; set; }
        public string Currency { get; set; } = "USD";
    }

    public class ResourceCostItem
    {
        public string ResourcePath { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public double Actual { get; set; }
        public double Forecast { get; set; }
        public string Currency { get; set; } = "USD";
        public string SubscriptionId { get; set; } = string.Empty;
    }

    // ── Tag Inventory ────────────────────────────────────────────────────────

    public class TagEntry
    {
        public int TotalResources { get; set; }
        public List<TagValue> Values { get; set; } = new();
    }

    public class TagValue
    {
        public string Value { get; set; } = string.Empty;
        public int ResourceCount { get; set; }
        public List<string> ResourceTypes { get; set; } = new();
    }

    public class UntaggedResource
    {
        public string ResourceName { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string Subscription { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
    }

    public class TagInventoryResult
    {
        public Dictionary<string, TagEntry> TagNames { get; set; } = new();
        public int TagCount { get; set; }
        public int UntaggedCount { get; set; }
        public double TagCoverage { get; set; }
        public int TotalResources { get; set; }
        public int TaggedCount { get; set; }
        public List<UntaggedResource> UntaggedResources { get; set; } = new();
        public Dictionary<string, List<string>> TagLocations { get; set; } = new();
    }

    // ── Cost By Tag ──────────────────────────────────────────────────────────

    public class CostByTagResult
    {
        public List<string> TagsQueried { get; set; } = new();
        public Dictionary<string, double> CostByTag { get; set; } = new();
        public bool NoTagsFound { get; set; }
    }

    // ── Azure Hybrid Benefit ─────────────────────────────────────────────────

    public class AhbItem
    {
        public string Name { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string CurrentLicense { get; set; } = string.Empty;
        public string VmSize { get; set; } = string.Empty;
        public string SqlEdition { get; set; } = string.Empty;
        public string OsType { get; set; } = string.Empty;
        public double? MaxSizeGb { get; set; }
        public string Sku { get; set; } = string.Empty;
    }

    public class AhbResult
    {
        public List<AhbItem> WindowsVMs { get; set; } = new();
        public List<AhbItem> SqlVMs { get; set; } = new();
        public List<AhbItem> SqlDatabases { get; set; } = new();
        public int TotalOpportunities { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    // ── Reservations ─────────────────────────────────────────────────────────

    public class ReservationAdvice
    {
        public string Subscription { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Problem { get; set; } = string.Empty;
        public string Solution { get; set; } = string.Empty;
        public string Impact { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public double? AnnualSavings { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string Term { get; set; } = string.Empty;
    }

    public class ReservationRecommendation
    {
        public string ResourceType { get; set; } = string.Empty;
        public string SKU { get; set; } = string.Empty;
        public double? RecommendedQty { get; set; }
        public string Term { get; set; } = string.Empty;
        public double? CostWithoutRI { get; set; }
        public double? CostWithRI { get; set; }
        public double? NetSavings { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string LookBackPeriod { get; set; } = string.Empty;
    }

    public class ReservationResult
    {
        public List<ReservationAdvice> AdvisorRecommendations { get; set; } = new();
        public List<ReservationRecommendation> ReservationRecommendations { get; set; } = new();
        public int TotalAdvisorCount { get; set; }
        public double EstimatedAnnualSavings { get; set; }
    }

    // ── Optimization Advice ──────────────────────────────────────────────────

    public class OptimizationRecommendation
    {
        public string Subscription { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Impact { get; set; } = string.Empty;
        public string Problem { get; set; } = string.Empty;
        public string Solution { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public double? AnnualSavings { get; set; }
        public string Currency { get; set; } = string.Empty;
    }

    public class OptimizationResult
    {
        public List<OptimizationRecommendation> Recommendations { get; set; } = new();
        public List<CategorySummary> ByCategory { get; set; } = new();
        public List<ImpactSummary> ByImpact { get; set; } = new();
        public int TotalCount { get; set; }
        public double EstimatedAnnualSavings { get; set; }
    }

    public class CategorySummary
    {
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
        public double TotalSavings { get; set; }
    }

    public class ImpactSummary
    {
        public string Impact { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    // ── Tag Recommendations ──────────────────────────────────────────────────

    public class TagRecItem
    {
        public string Tag { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string Pillar { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string ActionLabel { get; set; } = string.Empty;
        public string ActionTagName { get; set; } = string.Empty;
    }

    public class TagRecsResult
    {
        public List<TagRecItem> Analysis { get; set; } = new();
        public int Present { get; set; }
        public double CompliancePercent { get; set; }
    }

    // ── Cost Trend ───────────────────────────────────────────────────────────

    public class CostTrendMonth
    {
        public string Month { get; set; } = string.Empty;
        public double Cost { get; set; }
        public string Currency { get; set; } = "USD";
    }

    public class CostTrendResult
    {
        public List<CostTrendMonth> Months { get; set; } = new();
        public bool HasData { get; set; }
    }

    // ── Billing Structure ────────────────────────────────────────────────────

    public class BillingAccount
    {
        public string DisplayName { get; set; } = string.Empty;
        public string AgreementType { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public string AccountStatus { get; set; } = string.Empty;
    }

    public class BillingProfile
    {
        public string DisplayName { get; set; } = string.Empty;
        public string BillingAccount { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public int InvoiceDay { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class InvoiceSection
    {
        public string DisplayName { get; set; } = string.Empty;
        public string BillingProfile { get; set; } = string.Empty;
        public string BillingAccount { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
    }

    public class BillingResult
    {
        public bool HasBillingAccess { get; set; }
        public List<BillingAccount> BillingAccounts { get; set; } = new();
        public List<BillingProfile> BillingProfiles { get; set; } = new();
        public List<InvoiceSection> InvoiceSections { get; set; } = new();
        public List<string> EADepartments { get; set; } = new();
        public List<string> CostAllocationRules { get; set; } = new();
    }

    // ── Commitment Utilization ───────────────────────────────────────────────

    public class CommitmentItem
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string SKU { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string Term { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double UsedQuantity { get; set; }
        public double UtilizationPct { get; set; }
        public string ExpiryDate { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
    }

    public class CommitmentResult
    {
        public List<CommitmentItem> Commitments { get; set; } = new();
        public int TotalCount { get; set; }
        public double AvgUtilization { get; set; }
    }

    // ── Orphaned Resources ───────────────────────────────────────────────────

    public class OrphanedResource
    {
        public string Category { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string Impact { get; set; } = string.Empty;
    }

    public class OrphanCategorySummary
    {
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class OrphanedResourcesResult
    {
        public List<OrphanedResource> Orphans { get; set; } = new();
        public List<OrphanCategorySummary> Summary { get; set; } = new();
        public int TotalCount { get; set; }
        public bool HasData { get; set; }
    }

    // ── Budget Status ────────────────────────────────────────────────────────

    public class BudgetItem
    {
        public string Subscription { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string BudgetName { get; set; } = string.Empty;
        public double Amount { get; set; }
        public string TimeGrain { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public double ActualSpend { get; set; }
        public double Forecast { get; set; }
        public double PctUsed { get; set; }
        public double PctForecast { get; set; }
        public string Risk { get; set; } = string.Empty;
        public string Thresholds { get; set; } = string.Empty;
        public string ContactEmails { get; set; } = string.Empty;
        public string ContactRoles { get; set; } = string.Empty;
        public string TagFilter { get; set; } = string.Empty;
        public string Currency { get; set; } = "USD";
    }

    public class BudgetResult
    {
        public List<BudgetItem> Budgets { get; set; } = new();
        public int TotalBudgets { get; set; }
        public int SubsWithBudget { get; set; }
        public int SubsWithoutBudget { get; set; }
        public int OverBudgetCount { get; set; }
        public int AtRiskCount { get; set; }
        public bool HasData { get; set; }
        public bool Sampled { get; set; }
        public double BudgetCoverage { get; set; }
    }

    // ── Anomaly Alerts ───────────────────────────────────────────────────────

    public class AnomalyAlert
    {
        public string Subscription { get; set; } = string.Empty;
        public string AlertName { get; set; } = string.Empty;
        public string AlertType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string TimeModified { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public class AnomalyAlertsResult
    {
        public List<AnomalyAlert> TriggeredAlerts { get; set; } = new();
        public List<AnomalyAlert> ConfiguredRules { get; set; } = new();
        public bool HasData { get; set; }
    }

    // ── Savings Realized ─────────────────────────────────────────────────────

    public class SavingsResult
    {
        public double TotalMonthly { get; set; }
        public double RISavingsMonthly { get; set; }
        public double SPSavingsMonthly { get; set; }
        public double AHBSavingsMonthly { get; set; }
    }

    // ── Policy Inventory ─────────────────────────────────────────────────────

    public class PolicyAssignment
    {
        public string AssignmentName { get; set; } = string.Empty;
        public string AssignmentId { get; set; } = string.Empty;
        public string PolicyDefId { get; set; } = string.Empty;
        public string Effect { get; set; } = string.Empty;
        public string EnforcementMode { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string Subscription { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
    }

    public class PolicyComplianceEntry
    {
        public string Subscription { get; set; } = string.Empty;
        public int Compliant { get; set; }
        public int NonCompliant { get; set; }
        public int TotalResources { get; set; }
    }

    public class PolicyInventoryResult
    {
        public List<PolicyAssignment> Assignments { get; set; } = new();
        public Dictionary<string, PolicyComplianceEntry> ComplianceBySubMap { get; set; } = new();
        public int TotalAssignments { get; set; }
        public int TotalNonCompliant { get; set; }
    }

    // ── Policy Recommendations ───────────────────────────────────────────────

    public class PolicyParam
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public bool Required { get; set; }
        public string DefaultValue { get; set; } = string.Empty;
    }

    public class PolicyRecItem
    {
        public string Policy { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string Pillar { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string Effect { get; set; } = string.Empty;
        public string DefaultEffect { get; set; } = string.Empty;
        public List<string> AllowedEffects { get; set; } = new();
        public string PolicyDefId { get; set; } = string.Empty;
        public List<PolicyParam> Parameters { get; set; } = new();
        public int PolicyIndex { get; set; }
    }

    public class PolicyRecsResult
    {
        public List<PolicyRecItem> Analysis { get; set; } = new();
        public int Assigned { get; set; }
        public double CompliancePct { get; set; }
    }

    // ── Storage Tier ─────────────────────────────────────────────────────────

    public class StorageTierItem
    {
        public string Name { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Tier { get; set; } = string.Empty;
        public double SizeGB { get; set; }
        public long TransactionCount { get; set; }
        public string Recommendation { get; set; } = string.Empty;
    }

    public class StorageTierResult
    {
        public List<StorageTierItem> StorageAccounts { get; set; } = new();
        public int Count { get; set; }
        public bool HasData { get; set; }
    }

    // ── Idle VMs ─────────────────────────────────────────────────────────────

    public class IdleVM
    {
        public string VMName { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string VMSize { get; set; } = string.Empty;
        public string OS { get; set; } = string.Empty;
        public double AvgCPU14d { get; set; }
        public string NetworkPerDay { get; set; } = string.Empty;
        public string Classification { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
    }

    public class IdleVMResult
    {
        public List<IdleVM> IdleVMs { get; set; } = new();
        public int Count { get; set; }
        public bool HasData { get; set; }
        public int ScannedVMs { get; set; }
    }

    // ── Anomaly (Cost) Items ─────────────────────────────────────────────────

    public class AnomalyItem
    {
        public string Subscription { get; set; } = string.Empty;
        public string Month { get; set; } = string.Empty;
        public double Cost { get; set; }
        public double PreviousCost { get; set; }
        public double ChangePercent { get; set; }
    }

    // ── Top-level Scan Data ──────────────────────────────────────────────────

    public class ScanData
    {
        // Auth
        public TenantInfo? Auth { get; set; }

        // Hierarchy
        public HierarchyResult? Hierarchy { get; set; }

        // Contract
        public List<ContractInfo> Contract { get; set; } = new();

        // Cost
        public Dictionary<string, SubscriptionCost> Costs { get; set; } = new();

        // Resource costs
        public List<ResourceCostItem> ResourceCosts { get; set; } = new();

        // Tags
        public TagInventoryResult? Tags { get; set; }
        public CostByTagResult? CostByTag { get; set; }
        public TagRecsResult? TagRecs { get; set; }

        // AHB
        public AhbResult? Ahb { get; set; }

        // Reservations
        public ReservationResult? Reservations { get; set; }

        // Optimization
        public OptimizationResult? Optimization { get; set; }

        // Trend
        public CostTrendResult? CostTrend { get; set; }

        // Billing
        public BillingResult? Billing { get; set; }

        // Commitments
        public CommitmentResult? Commitments { get; set; }

        // Orphaned
        public OrphanedResourcesResult? OrphanedResources { get; set; }

        // Budgets
        public BudgetResult? Budgets { get; set; }

        // Anomaly alerts
        public AnomalyAlertsResult? AnomalyAlerts { get; set; }

        // Savings
        public SavingsResult? Savings { get; set; }

        // Policy
        public PolicyInventoryResult? PolicyInventory { get; set; }
        public PolicyRecsResult? PolicyRecs { get; set; }

        // Storage
        public StorageTierResult? StorageTier { get; set; }

        // Idle VMs
        public IdleVMResult? IdleVMs { get; set; }

        // Scan metadata
        public System.DateTime ScanStarted { get; set; } = System.DateTime.UtcNow;
        public System.DateTime? ScanCompleted { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
