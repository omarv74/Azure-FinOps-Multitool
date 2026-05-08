using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Azure.Identity;
using Azure.Core;
using FinOpsMultitool.Helpers;
using FinOpsMultitool.Models;
using FinOpsMultitool.Services;
using Microsoft.Win32;

namespace FinOpsMultitool
{
    public partial class MainWindow : Window
    {
        // ── State ─────────────────────────────────────────────────────────────
        private ScanData _scanData = new();
        private TokenCredential? _credential;

        // ── Services ──────────────────────────────────────────────────────────
        private AzureRestService? _restService;
        private ResourceGraphService? _graphService;
        private AuthService? _authService;
        private TenantHierarchyService? _hierarchyService;
        private ContractInfoService? _contractService;
        private CostManagementService? _costService;
        private ResourceCostService? _resourceCostService;
        private CostTrendService? _costTrendService;
        private CostByTagService? _costByTagService;
        private AnomalyAlertService? _anomalyAlertService;
        private TagInventoryService? _tagInventoryService;
        private TagRecommendationsService? _tagRecsService;
        private TagDeployService? _tagDeployService;
        private AhbService? _ahbService;
        private ReservationService? _reservationService;
        private CommitmentService? _commitmentService;
        private OptimizationService? _optimizationService;
        private OrphanedResourcesService? _orphanService;
        private StorageTierService? _storageTierService;
        private IdleVMService? _idleVMService;
        private BudgetService? _budgetService;
        private BillingService? _billingService;
        private SavingsService? _savingsService;
        private PolicyInventoryService? _policyInventoryService;
        private PolicyRecommendationsService? _policyRecsService;
        private PolicyDeployService? _policyDeployService;

        // ── Policy deploy panel state ─────────────────────────────────────────
        private string _currentPolicyDefId = string.Empty;
        private string _currentPolicyDisplayName = string.Empty;
        private string _currentPolicyAssignmentId = string.Empty;
        private bool _policyUnassignMode = false;

        // ── Tag deploy panel state ────────────────────────────────────────────
        private string _tagDeployMode = "add";
        private string _tagDeployTagName = string.Empty;

        // ─────────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            WireEventHandlers();
            PopulateResourcesTab();
        }

        // ── Wire event handlers ───────────────────────────────────────────────
        private void WireEventHandlers()
        {
            TenantButton.Click += TenantButton_Click;
            GovTenantButton.Click += GovTenantButton_Click;
            ScanButton.Click += ScanButton_Click;
            ExportButton.Click += ExportButton_Click;

            TrendSubSelector.SelectionChanged += TrendSubSelector_SelectionChanged;
            TagSelector.SelectionChanged += TagSelector_SelectionChanged;
            BudgetSubSelector.SelectionChanged += BudgetSubSelector_SelectionChanged;

            CustomTagButton.Click += CustomTagButton_Click;
            TagDeployButton.Click += TagDeployButton_Click;
            TagDeployCancelButton.Click += TagDeployCancelButton_Click;

            PolicyDeployButton.Click += PolicyDeployButton_Click;
            PolicyDeployCancelButton.Click += PolicyDeployCancelButton_Click;
            PolicyRemediateButton.Click += PolicyRemediateButton_Click;

            BudgetDeployButton.Click += BudgetDeployButton_Click;
            BudgetDeployCancelButton.Click += BudgetDeployCancelButton_Click;
            BudgetPolicyDeployButton.Click += BudgetPolicyDeployButton_Click;
            BudgetPolicyCancelButton.Click += BudgetPolicyCancelButton_Click;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Connect handlers
        // ─────────────────────────────────────────────────────────────────────
        private async void TenantButton_Click(object sender, RoutedEventArgs e)
            => await ConnectAsync("AzureCloud");

        private async void GovTenantButton_Click(object sender, RoutedEventArgs e)
            => await ConnectAsync("AzureUSGovernment");

        private async Task ConnectAsync(string environment)
        {
            try
            {
                TenantButton.IsEnabled = false;
                GovTenantButton.IsEnabled = false;
                ScanButton.IsEnabled = false;
                UpdateStatus("Authenticating…", 10);

                var credOptions = new InteractiveBrowserCredentialOptions();
                if (environment == "AzureUSGovernment")
                    credOptions.AuthorityHost = AzureAuthorityHosts.AzureGovernment;

                _credential = new InteractiveBrowserCredential(credOptions);
                _restService = new AzureRestService(_credential, environment);
                _graphService = new ResourceGraphService(_credential);
                _authService = new AuthService(_restService);
                _authService.StatusCallback = msg => UpdateStatus(msg, 20);

                var tenantInfo = await Task.Run(() => _authService.ConnectAsync(environment));
                if (tenantInfo == null)
                {
                    UpdateStatus("Authentication failed.", 0);
                    return;
                }

                _scanData = new ScanData { Auth = tenantInfo };
                InitializeServices();

                Dispatcher.Invoke(() =>
                {
                    TenantLabel.Text =
                        $"Tenant: {tenantInfo.TenantId}  |  {tenantInfo.Subscriptions.Count} subscription(s)";
                    ScanButton.IsEnabled = true;
                    ExportButton.IsEnabled = false;
                    UpdateStatus(
                        $"Connected – {tenantInfo.Subscriptions.Count} subscription(s) ({tenantInfo.TenantSize} tenant)",
                        100);
                });

                // Load hierarchy in background
                await Task.Run(async () =>
                {
                    try
                    {
                        _scanData.Hierarchy = await _hierarchyService!.GetHierarchyAsync(
                            tenantInfo.TenantId, tenantInfo.Subscriptions);
                        Dispatcher.Invoke(PopulateHierarchyTree);
                    }
                    catch { /* hierarchy is optional */ }
                });
            }
            catch (Exception ex)
            {
                UpdateStatus($"Connection failed: {ex.Message}", 0);
                MessageBox.Show($"Authentication error:\n{ex.Message}", "Connection Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    TenantButton.IsEnabled = true;
                    GovTenantButton.IsEnabled = true;
                });
            }
        }

        private void InitializeServices()
        {
            if (_restService == null || _graphService == null) return;

            _hierarchyService = new TenantHierarchyService(_restService);
            _contractService = new ContractInfoService(_restService);
            _costService = new CostManagementService(_restService);
            _resourceCostService = new ResourceCostService(_restService);
            _costTrendService = new CostTrendService(_restService);
            _costByTagService = new CostByTagService(_restService);
            _anomalyAlertService = new AnomalyAlertService(_restService);
            _tagInventoryService = new TagInventoryService(_restService, _graphService);
            _tagRecsService = new TagRecommendationsService();
            _tagDeployService = new TagDeployService(_restService);
            _ahbService = new AhbService(_graphService);
            _reservationService = new ReservationService(_restService, _graphService);
            _commitmentService = new CommitmentService(_restService);
            _optimizationService = new OptimizationService(_restService, _graphService);
            _orphanService = new OrphanedResourcesService(_graphService);
            _storageTierService = new StorageTierService(_restService, _graphService);
            _idleVMService = new IdleVMService(_restService, _graphService);
            _budgetService = new BudgetService(_restService);
            _billingService = new BillingService(_restService);
            _savingsService = new SavingsService(_restService);
            _policyInventoryService = new PolicyInventoryService(_restService, _graphService);
            _policyRecsService = new PolicyRecommendationsService();
            _policyDeployService = new PolicyDeployService(_restService);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Scan pipeline
        // ─────────────────────────────────────────────────────────────────────
        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scanData.Auth == null)
            {
                MessageBox.Show("Please connect to a tenant first.", "Not Connected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ScanButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            _scanData.ScanStarted = DateTime.UtcNow;
            _scanData.Warnings.Clear();

            var subs = _scanData.Auth.Subscriptions;
            string tenantId = _scanData.Auth.TenantId;

            try
            {
                await Task.Run(async () =>
                {
                    var ct = CancellationToken.None;

                    // 1 – Contract / agreement type
                    UpdateStatus("Step 1/20 – Contract info…", 5);
                    try { _scanData.Contract = await _contractService!.GetContractInfoAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Contract: {ex.Message}"); }

                    // 2 – Tenant hierarchy
                    UpdateStatus("Step 2/20 – Tenant hierarchy…", 8);
                    try
                    {
                        if (_scanData.Hierarchy == null)
                        {
                            _scanData.Hierarchy = await _hierarchyService!.GetHierarchyAsync(tenantId, subs, ct);
                            Dispatcher.Invoke(PopulateHierarchyTree);
                        }
                    }
                    catch (Exception ex) { _scanData.Warnings.Add($"Hierarchy: {ex.Message}"); }

                    // 3 – Subscription costs
                    UpdateStatus("Step 3/20 – Subscription costs…", 12);
                    try { _scanData.Costs = await _costService!.GetCostDataAsync(tenantId, subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Costs: {ex.Message}"); }

                    // 4 – Resource costs
                    UpdateStatus("Step 4/20 – Resource costs (top 100/sub)…", 17);
                    try { _scanData.ResourceCosts = await _resourceCostService!.GetResourceCostsAsync(tenantId, subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Resource costs: {ex.Message}"); }

                    // 5 – Cost trend (6 months)
                    UpdateStatus("Step 5/20 – Cost trend (6 months)…", 22);
                    try { _scanData.CostTrend = await _costTrendService!.GetCostTrendAsync(tenantId, subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Cost trend: {ex.Message}"); }

                    // 6 – Anomaly alerts
                    UpdateStatus("Step 6/20 – Cost anomaly alerts…", 27);
                    try { _scanData.AnomalyAlerts = await _anomalyAlertService!.GetAnomalyAlertsAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Anomaly alerts: {ex.Message}"); }

                    // 7 – Tag inventory
                    UpdateStatus("Step 7/20 – Tag inventory…", 32);
                    try { _scanData.Tags = await _tagInventoryService!.GetTagInventoryAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Tags: {ex.Message}"); }

                    // 8 – Cost by tag
                    UpdateStatus("Step 8/20 – Cost by tag…", 36);
                    try
                    {
                        if (_scanData.Tags?.TagNames?.Count > 0)
                        {
                            var topTags = _scanData.Tags.TagNames.Keys.Take(10).ToList();
                            _scanData.CostByTag = await _costByTagService!.GetCostByTagAsync(tenantId, subs, _scanData.Tags, ct);
                        }
                    }
                    catch (Exception ex) { _scanData.Warnings.Add($"Cost by tag: {ex.Message}"); }

                    // 9 – Tag recommendations
                    UpdateStatus("Step 9/20 – Tag recommendations…", 40);
                    try
                    {
                        if (_scanData.Tags != null)
                            _scanData.TagRecs = await _tagRecsService!.GetTagRecommendationsAsync(_scanData.Tags, ct);
                    }
                    catch (Exception ex) { _scanData.Warnings.Add($"Tag recs: {ex.Message}"); }

                    // 10 – AHB opportunities
                    UpdateStatus("Step 10/20 – AHB opportunities…", 44);
                    try { _scanData.Ahb = await _ahbService!.GetAhbOpportunitiesAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"AHB: {ex.Message}"); }

                    // 11 – Commitment utilization
                    UpdateStatus("Step 11/20 – Commitment utilization…", 48);
                    try { _scanData.Commitments = await _commitmentService!.GetCommitmentUtilizationAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Commitments: {ex.Message}"); }

                    // 12 – Reservation advice
                    UpdateStatus("Step 12/20 – Reservation advice…", 52);
                    try { _scanData.Reservations = await _reservationService!.GetReservationAdviceAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Reservations: {ex.Message}"); }

                    // 13 – Advisor / optimization
                    UpdateStatus("Step 13/20 – Advisor recommendations…", 56);
                    try { _scanData.Optimization = await _optimizationService!.GetOptimizationAdviceAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Optimization: {ex.Message}"); }

                    // 14 – Orphaned resources
                    UpdateStatus("Step 14/20 – Orphaned resources…", 60);
                    try { _scanData.OrphanedResources = await _orphanService!.GetOrphanedResourcesAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Orphans: {ex.Message}"); }

                    // 15 – Storage tier
                    UpdateStatus("Step 15/20 – Storage tier analysis…", 64);
                    try { _scanData.StorageTier = await _storageTierService!.GetStorageTierAdviceAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Storage tier: {ex.Message}"); }

                    // 16 – Idle VMs
                    UpdateStatus("Step 16/20 – Idle VM detection…", 68);
                    try { _scanData.IdleVMs = await _idleVMService!.GetIdleVMsAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Idle VMs: {ex.Message}"); }

                    // 17 – Budgets
                    UpdateStatus("Step 17/20 – Budget analysis…", 72);
                    try { _scanData.Budgets = await _budgetService!.GetBudgetStatusAsync(subs, null, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Budgets: {ex.Message}"); }

                    // 18 – Billing structure
                    UpdateStatus("Step 18/20 – Billing structure…", 76);
                    try { _scanData.Billing = await _billingService!.GetBillingStructureAsync(ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Billing: {ex.Message}"); }

                    // 19 – Savings realized
                    UpdateStatus("Step 19/20 – Savings realized…", 80);
                    try { _scanData.Savings = await _savingsService!.GetSavingsRealizedAsync(subs, ct); }
                    catch (Exception ex) { _scanData.Warnings.Add($"Savings: {ex.Message}"); }

                    // 20 – Policy inventory & recommendations
                    UpdateStatus("Step 20/20 – Policy analysis…", 88);
                    try
                    {
                        _scanData.PolicyInventory = await _policyInventoryService!.GetPolicyInventoryAsync(subs, ct);
                        _scanData.PolicyRecs = await _policyRecsService!.GetPolicyRecommendationsAsync(
                            _scanData.PolicyInventory, ct);
                    }
                    catch (Exception ex) { _scanData.Warnings.Add($"Policy: {ex.Message}"); }

                    _scanData.ScanCompleted = DateTime.UtcNow;
                    UpdateStatus("Scan complete – populating tabs…", 95);
                });

                PopulateAllTabs();
                ExportButton.IsEnabled = true;

                double elapsed = (_scanData.ScanCompleted - _scanData.ScanStarted)?.TotalSeconds ?? 0;
                UpdateStatus(
                    $"Scan complete. Duration: {elapsed:F0}s  |  Warnings: {_scanData.Warnings.Count}",
                    100);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Scan failed: {ex.Message}", 0);
                MessageBox.Show($"Scan error:\n{ex.Message}", "Scan Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ScanButton.IsEnabled = true;
            }
        }

        private void PopulateAllTabs()
        {
            PopulateOverviewTab();
            PopulateCostTab();
            PopulateTagsTab();
            PopulatePolicyTab();
            PopulateOptimizationTab();
            PopulateBillingTab();
            PopulateBudgetsTab();
            PopulateGuidanceTab();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Export
        // ─────────────────────────────────────────────────────────────────────
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export Scan Results",
                Filter = "JSON Report (*.json)|*.json|HTML Report (*.html)|*.html|CSV Data (*.csv)|*.csv",
                FileName = $"FinOpsReport_{DateTime.Now:yyyyMMdd_HHmm}"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                string path = dlg.FileName;
                int idx = dlg.FilterIndex;

                if (idx == 1 || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    ExportHelper.ExportToJson(_scanData, path);
                else if (idx == 2 || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    ExportHelper.ExportToHtml(_scanData, path);
                else
                    ExportHelper.ExportToCsv(_scanData, path);

                MessageBox.Show($"Export saved:\n{path}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Status helper
        // ─────────────────────────────────────────────────────────────────────
        private void UpdateStatus(string msg, int percent)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = msg;
                ProgressBar.Value = Math.Clamp(percent, 0, 100);
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Overview tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateOverviewTab()
        {
            string currCode = _scanData.Costs.Values.FirstOrDefault()?.Currency ?? "USD";
            string sym = CurrencyHelper.GetSymbol(currCode);

            double totalActual = _scanData.Costs.Values.Sum(c => c.Actual);
            double totalForecast = _scanData.Costs.Values.Sum(c => c.Forecast);

            TotalCostText.Text = $"{sym}{totalActual:N2}";
            ForecastText.Text = $"{sym}{totalForecast:N2}";
            SubCountText.Text = (_scanData.Auth?.Subscriptions.Count ?? 0).ToString();

            // Contract cards
            if (_scanData.Contract?.Count > 0)
            {
                ContractTypeText.Text = _scanData.Contract[0].FriendlyType;
                ContractDetailText.Text = _scanData.Contract[0].AccountName;
            }

            // Estimated annual savings
            double estSavings = (_scanData.Optimization?.EstimatedAnnualSavings ?? 0)
                              + (_scanData.Reservations?.EstimatedAnnualSavings ?? 0);
            TotalSavingsText.Text = estSavings > 0 ? $"{sym}{estSavings:N0}/yr" : "–";

            // Savings realized
            double savingsMonthly = _scanData.Savings?.TotalMonthly ?? 0;
            SavingsRealizedText.Text = savingsMonthly > 0 ? $"{sym}{savingsMonthly:N0}/mo" : "–";
            if (_scanData.Savings != null)
            {
                SavingsRealizedDetail.Text =
                    $"RI:{sym}{_scanData.Savings.RISavingsMonthly:N0}  " +
                    $"SP:{sym}{_scanData.Savings.SPSavingsMonthly:N0}  " +
                    $"AHB:{sym}{_scanData.Savings.AHBSavingsMonthly:N0}";
            }

            // Subscription cost grid
            SubCostGrid.AutoGenerateColumns = true;
            SubCostGrid.ItemsSource = _scanData.Costs
                .Select(kv =>
                {
                    string name = _scanData.Auth?.Subscriptions
                        .FirstOrDefault(s => s.Id == kv.Key)?.Name ?? kv.Key;
                    string s = CurrencyHelper.GetSymbol(kv.Value.Currency);
                    return new
                    {
                        Subscription = name,
                        SubscriptionId = kv.Key,
                        MTDActual = $"{s}{kv.Value.Actual:N2}",
                        Forecast = $"{s}{kv.Value.Forecast:N2}",
                        Currency = kv.Value.Currency
                    };
                })
                .OrderByDescending(r =>
                    _scanData.Costs.TryGetValue(r.SubscriptionId, out var c2) ? c2.Actual : 0)
                .ToList();

            // Resource cost grid
            if (_scanData.ResourceCosts?.Count > 0)
            {
                ResourceCountNote.Text =
                    $"Showing top {_scanData.ResourceCosts.Count} resources by spend across all subscriptions.";
                ResourceCostGrid.AutoGenerateColumns = true;
                ResourceCostGrid.ItemsSource = _scanData.ResourceCosts
                    .OrderByDescending(r => r.Actual)
                    .Select(r => new
                    {
                        Resource = r.ResourcePath.Split('/').LastOrDefault() ?? r.ResourcePath,
                        r.ResourceGroup,
                        Type = r.ResourceType.Split('/').LastOrDefault() ?? r.ResourceType,
                        r.SubscriptionId,
                        Actual = $"{CurrencyHelper.GetSymbol(r.Currency)}{r.Actual:N2}",
                        r.Currency
                    })
                    .ToList();
            }
            else
            {
                ResourceCountNote.Text = "No resource cost data available.";
            }

            PopulateScorecard();
        }

        private void PopulateScorecard()
        {
            if (_scanData.Auth?.Subscriptions == null) return;

            var rows = _scanData.Auth.Subscriptions.Select(sub =>
            {
                // Cost trend
                string trend = "–";
                if (_scanData.CostTrend?.Months.Count >= 2)
                {
                    var months = _scanData.CostTrend.Months;
                    double last = months[^1].Cost;
                    double prev = months[^2].Cost;
                    trend = prev == 0 ? "–" :
                            last > prev * 1.10 ? "↑ Rising" :
                            last < prev * 0.90 ? "↓ Falling" : "→ Stable";
                }

                // Tag coverage
                string tagCoverage = _scanData.Tags != null ? $"{_scanData.Tags.TagCoverage:F0}%" : "–";

                // Budget status for this sub
                string budgetStatus = "No Budget";
                if (_scanData.Budgets?.Budgets?.Count > 0)
                {
                    var subBudgets = _scanData.Budgets.Budgets
                        .Where(b => b.SubscriptionId == sub.Id).ToList();
                    if (subBudgets.Any())
                    {
                        var worst = subBudgets.OrderByDescending(b => b.PctUsed).First();
                        budgetStatus = worst.Risk;
                    }
                }

                // Optimization item count
                int optCount = _scanData.Optimization?.Recommendations
                    .Count(r => r.SubscriptionId == sub.Id) ?? 0;

                // Policy compliance
                string policyStatus = "–";
                if (_scanData.PolicyInventory?.ComplianceBySubMap.TryGetValue(sub.Id, out var comp) == true)
                {
                    policyStatus = comp.TotalResources > 0
                        ? $"{(double)comp.Compliant / comp.TotalResources * 100:F0}%"
                        : "100%";
                }

                return new
                {
                    Subscription = sub.Name,
                    CostTrend = trend,
                    TagCoverage = tagCoverage,
                    BudgetStatus = budgetStatus,
                    OptimizationItems = optCount,
                    PolicyCompliance = policyStatus
                };
            }).ToList<object>();

            ScorecardGrid.AutoGenerateColumns = true;
            ScorecardGrid.ItemsSource = rows;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cost Analysis tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateCostTab()
        {
            // Trend subscription selector
            TrendSubSelector.SelectionChanged -= TrendSubSelector_SelectionChanged;
            TrendSubSelector.Items.Clear();
            TrendSubSelector.Items.Add("All Subscriptions");
            foreach (var sub in _scanData.Auth?.Subscriptions ?? new List<SubscriptionInfo>())
                TrendSubSelector.Items.Add(sub.Name);
            TrendSubSelector.SelectedIndex = 0;
            TrendSubSelector.SelectionChanged += TrendSubSelector_SelectionChanged;

            DrawTrendChart(null);

            // Anomaly detection from trend data
            if (_scanData.CostTrend?.Months.Count >= 2)
            {
                var anomalies = new List<object>();
                var months = _scanData.CostTrend.Months;
                for (int i = 1; i < months.Count; i++)
                {
                    if (months[i - 1].Cost > 0)
                    {
                        double pct = (months[i].Cost - months[i - 1].Cost) / months[i - 1].Cost * 100;
                        if (Math.Abs(pct) >= 25)
                        {
                            anomalies.Add(new
                            {
                                Month = months[i].Month,
                                Cost = CurrencyHelper.Format(months[i].Cost, months[i].Currency),
                                PreviousCost = CurrencyHelper.Format(months[i - 1].Cost, months[i - 1].Currency),
                                ChangePercent = $"{pct:+0.0;-0.0}%",
                                Direction = pct > 0 ? "↑ Spike" : "↓ Drop"
                            });
                        }
                    }
                }
                AnomalyGrid.AutoGenerateColumns = true;
                AnomalyGrid.ItemsSource = anomalies.Count > 0 ? anomalies : null;
                AnomalyNote.Text = anomalies.Count > 0
                    ? $"{anomalies.Count} anomaly period(s) detected (±25% month-over-month)."
                    : "No significant cost anomalies detected (±25% threshold).";
            }

            // Anomaly alerts from API
            if (_scanData.AnomalyAlerts != null)
            {
                AlertsSummaryNote.Text =
                    $"{_scanData.AnomalyAlerts.TriggeredAlerts.Count} triggered alert(s), " +
                    $"{_scanData.AnomalyAlerts.ConfiguredRules.Count} configured rule(s).";
                TriggeredAlertsGrid.AutoGenerateColumns = true;
                TriggeredAlertsGrid.ItemsSource = _scanData.AnomalyAlerts.TriggeredAlerts.Count > 0
                    ? _scanData.AnomalyAlerts.TriggeredAlerts : null;
                ConfiguredRulesGrid.AutoGenerateColumns = true;
                ConfiguredRulesGrid.ItemsSource = _scanData.AnomalyAlerts.ConfiguredRules.Count > 0
                    ? _scanData.AnomalyAlerts.ConfiguredRules : null;
            }

            // Cost by tag selector
            TagSelector.SelectionChanged -= TagSelector_SelectionChanged;
            TagSelector.Items.Clear();
            if (_scanData.CostByTag?.CostByTag?.Count > 0)
            {
                foreach (var tag in _scanData.CostByTag.CostByTag.Keys)
                    TagSelector.Items.Add(tag);
                TagSelector.SelectedIndex = 0;
                NoTagsLabel.Text = string.Empty;
            }
            else
            {
                NoTagsLabel.Text = _scanData.CostByTag?.NoTagsFound == true
                    ? "No tagged cost data found. Ensure resources are tagged and Cost Management has data."
                    : "Cost-by-tag data not available for this scan.";
            }
            TagSelector.SelectionChanged += TagSelector_SelectionChanged;
            UpdateCostByTagGrid();
        }

        private void TrendSubSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrendSubSelector.SelectedItem == null) return;
            string selected = TrendSubSelector.SelectedItem.ToString() ?? string.Empty;
            if (selected == "All Subscriptions")
            {
                DrawTrendChart(null);
            }
            else
            {
                var sub = _scanData.Auth?.Subscriptions.FirstOrDefault(s => s.Name == selected);
                DrawTrendChart(sub?.Id);
            }
        }

        private void TagSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateCostByTagGrid();

        private void UpdateCostByTagGrid()
        {
            if (_scanData.CostByTag?.CostByTag == null || TagSelector.SelectedItem == null)
            {
                CostByTagGrid.ItemsSource = null;
                return;
            }

            string sym = CurrencyHelper.GetSymbol(_scanData.Costs.Values.FirstOrDefault()?.Currency ?? "USD");
            var rows = _scanData.CostByTag.CostByTag
                .Select(kv => new
                {
                    Tag = kv.Key,
                    Cost = $"{sym}{kv.Value:N2}",
                    Amount = kv.Value
                })
                .OrderByDescending(r => r.Amount)
                .Select(r => new { r.Tag, r.Cost })
                .ToList();

            CostByTagGrid.AutoGenerateColumns = true;
            CostByTagGrid.ItemsSource = rows;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Trend chart (Canvas drawing)
        // ─────────────────────────────────────────────────────────────────────
        private void DrawTrendChart(string? subscriptionId)
        {
            TrendChart.Children.Clear();

            var months = _scanData.CostTrend?.Months;
            if (months == null || months.Count == 0)
            {
                var noData = new TextBlock
                {
                    Text = "No trend data available. Run a scan first.",
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    FontSize = 13
                };
                Canvas.SetLeft(noData, 80);
                Canvas.SetTop(noData, 90);
                TrendChart.Children.Add(noData);
                TrendNote.Text = string.Empty;
                return;
            }

            double chartWidth = TrendChart.ActualWidth > 0 ? TrendChart.ActualWidth : 900;
            const double padLeft = 75, padRight = 20, padTop = 18, padBottom = 38;
            double plotW = chartWidth - padLeft - padRight;
            const double plotH = 200 - padTop - padBottom;   // TrendChart Height = 220

            double maxCost = months.Max(m => m.Cost);
            if (maxCost == 0) maxCost = 1;

            string sym = CurrencyHelper.GetSymbol(months[0].Currency);

            // Horizontal grid lines + Y-axis labels
            for (int i = 0; i <= 4; i++)
            {
                double y = padTop + plotH - plotH * i / 4;
                TrendChart.Children.Add(new Line
                {
                    X1 = padLeft, Y1 = y, X2 = padLeft + plotW, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                    StrokeThickness = 1
                });

                double val = maxCost * i / 4;
                var yLabel = new TextBlock
                {
                    Text = val >= 1000 ? $"{sym}{val / 1000:F1}K" : $"{sym}{val:F0}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120))
                };
                Canvas.SetLeft(yLabel, 0);
                Canvas.SetTop(yLabel, y - 8);
                TrendChart.Children.Add(yLabel);
            }

            // Compute points
            var points = new PointCollection();
            for (int i = 0; i < months.Count; i++)
            {
                double x = padLeft + i * plotW / Math.Max(months.Count - 1, 1);
                double y = padTop + plotH - months[i].Cost / maxCost * plotH;
                points.Add(new Point(x, y));
            }

            // Area fill (inserted before the line)
            var areaPoints = new PointCollection(points);
            areaPoints.Add(new Point(padLeft + plotW, padTop + plotH));
            areaPoints.Add(new Point(padLeft, padTop + plotH));
            TrendChart.Children.Add(new Polygon
            {
                Points = areaPoints,
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212)),
                Stroke = Brushes.Transparent
            });

            // Polyline
            TrendChart.Children.Add(new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round
            });

            // X-axis
            TrendChart.Children.Add(new Line
            {
                X1 = padLeft, Y1 = padTop + plotH,
                X2 = padLeft + plotW, Y2 = padTop + plotH,
                Stroke = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                StrokeThickness = 1
            });

            // Data points, month labels, value labels
            for (int i = 0; i < months.Count; i++)
            {
                double x = padLeft + i * plotW / Math.Max(months.Count - 1, 1);
                double y = padTop + plotH - months[i].Cost / maxCost * plotH;

                var dot = new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                    Stroke = Brushes.White, StrokeThickness = 1.5
                };
                Canvas.SetLeft(dot, x - 4);
                Canvas.SetTop(dot, y - 4);
                TrendChart.Children.Add(dot);

                var monthLbl = new TextBlock
                {
                    Text = months[i].Month, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    Width = 42, TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(monthLbl, x - 21);
                Canvas.SetTop(monthLbl, padTop + plotH + 5);
                TrendChart.Children.Add(monthLbl);

                string valText = months[i].Cost >= 1000
                    ? $"{sym}{months[i].Cost / 1000:F1}K"
                    : $"{sym}{months[i].Cost:F0}";
                var valLbl = new TextBlock
                {
                    Text = valText, FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80))
                };
                Canvas.SetLeft(valLbl, x - 18);
                Canvas.SetTop(valLbl, y - 16);
                TrendChart.Children.Add(valLbl);
            }

            TrendNote.Text = subscriptionId == null
                ? "Aggregate cost trend across all subscriptions."
                : $"Cost trend for subscription {subscriptionId}.";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tags tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateTagsTab()
        {
            if (_scanData.Tags == null) return;

            TagCountText.Text = _scanData.Tags.TagCount.ToString();
            TagCoverageText.Text = $"{_scanData.Tags.TagCoverage:F1}%";
            UntaggedCountText.Text = _scanData.Tags.UntaggedCount.ToString();

            // Tag inventory
            TagInventoryGrid.AutoGenerateColumns = true;
            TagInventoryGrid.ItemsSource = _scanData.Tags.TagNames
                .Select(kv => new
                {
                    TagName = kv.Key,
                    UniqueValues = kv.Value.Values.Count,
                    TotalResources = kv.Value.TotalResources
                })
                .OrderByDescending(r => r.TotalResources)
                .ToList();

            // Untagged resources
            UntaggedNote.Text =
                $"{_scanData.Tags.UntaggedResources.Count} untagged resource(s) found.";
            UntaggedResourcesGrid.AutoGenerateColumns = true;
            UntaggedResourcesGrid.ItemsSource = _scanData.Tags.UntaggedResources.Count > 0
                ? _scanData.Tags.UntaggedResources : null;

            // Tag compliance
            if (_scanData.TagRecs != null)
            {
                TagComplianceText.Text =
                    $"{_scanData.TagRecs.Present} of {_scanData.TagRecs.Analysis.Count} CAF tags present " +
                    $"({_scanData.TagRecs.CompliancePercent:F0}% compliance)";
            }

            PopulateTagRecsGrid();
        }

        private void PopulateTagRecsGrid()
        {
            TagRecsGrid.Columns.Clear();
            TagRecsGrid.AutoGenerateColumns = false;

            TagRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Tag",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new System.Windows.Data.Binding("Tag")
            });
            TagRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Status", Width = 90,
                Binding = new System.Windows.Data.Binding("Status")
            });
            TagRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Priority", Width = 90,
                Binding = new System.Windows.Data.Binding("Priority")
            });
            TagRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Pillar", Width = 95,
                Binding = new System.Windows.Data.Binding("Pillar")
            });
            TagRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Purpose",
                Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                Binding = new System.Windows.Data.Binding("Purpose")
            });

            var actionCol = new DataGridTemplateColumn { Header = "Action", Width = 130 };
            var tmpl = new DataTemplate();
            var btnFactory = new FrameworkElementFactory(typeof(Button));
            btnFactory.SetBinding(Button.ContentProperty, new System.Windows.Data.Binding("ActionLabel"));
            btnFactory.SetValue(Button.PaddingProperty, new Thickness(6, 2, 6, 2));
            btnFactory.SetValue(Button.MarginProperty, new Thickness(2));
            btnFactory.SetValue(Button.FontSizeProperty, 11.0);
            btnFactory.SetValue(Button.CursorProperty, Cursors.Hand);
            btnFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler(TagRecAction_Click));
            tmpl.VisualTree = btnFactory;
            actionCol.CellTemplate = tmpl;
            TagRecsGrid.Columns.Add(actionCol);

            TagRecsGrid.ItemsSource = _scanData.TagRecs?.Analysis ?? new List<TagRecItem>();
        }

        private void TagRecAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not TagRecItem item) return;
            string mode = item.Status == "Present" ? "remove" : "add";
            _ = ShowTagDeployPanelAsync(item.ActionTagName.Length > 0 ? item.ActionTagName : item.Tag, mode);
        }

        private void CustomTagButton_Click(object sender, RoutedEventArgs e)
        {
            TagNameLabel.Visibility = Visibility.Visible;
            TagNameInput.Visibility = Visibility.Visible;
            TagNameInput.Text = string.Empty;
            _ = ShowTagDeployPanelAsync(string.Empty, "add");
            TagDeployTitle.Text = "Deploy Custom Tag";
        }

        private async Task ShowTagDeployPanelAsync(string tagName, string mode)
        {
            _tagDeployMode = mode;
            _tagDeployTagName = tagName;

            TagDeployPanel.Visibility = Visibility.Visible;
            TagDeployStatus.Text = string.Empty;

            if (mode == "add")
            {
                TagDeployTitle.Text = $"Deploy Tag: {tagName}";
                TagNameInput.Text = tagName;
                TagValueInput.IsEnabled = true;
                TagValueInput.Text = string.Empty;
            }
            else
            {
                TagDeployTitle.Text = $"Remove Tag: {tagName}";
                TagNameInput.Text = tagName;
                TagValueInput.IsEnabled = false;
                TagValueInput.Text = "(tag will be removed)";
            }

            TagScopeSelector.Items.Clear();
            if (_tagDeployService != null && _scanData.Auth?.Subscriptions != null)
            {
                try
                {
                    var scopes = await _tagDeployService.GetScopesAsync(_scanData.Auth.Subscriptions);
                    foreach (var (displayName, resourceId) in scopes)
                        TagScopeSelector.Items.Add(
                            new ComboBoxItem { Content = displayName, Tag = resourceId });
                    if (TagScopeSelector.Items.Count > 0) TagScopeSelector.SelectedIndex = 0;
                }
                catch (Exception ex)
                {
                    TagDeployStatus.Text = $"Error loading scopes: {ex.Message}";
                }
            }
        }

        private async void TagDeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tagDeployService == null) { TagDeployStatus.Text = "Not connected."; return; }

            string tagName = TagNameInput.Visibility == Visibility.Visible
                ? TagNameInput.Text.Trim()
                : _tagDeployTagName;
            string tagValue = TagValueInput.Text.Trim();
            string scope = (TagScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(tagName)) { TagDeployStatus.Text = "Enter a tag name."; return; }
            if (string.IsNullOrEmpty(scope)) { TagDeployStatus.Text = "Select a scope."; return; }

            TagDeployButton.IsEnabled = false;
            TagDeployStatus.Text = "Deploying…";

            try
            {
                (bool Success, string Message) result = _tagDeployMode == "remove"
                    ? await _tagDeployService.RemoveTagAsync(tagName, scope)
                    : await _tagDeployService.DeployTagAsync(tagName, tagValue, scope);

                TagDeployStatus.Text = result.Message;
                TagDeployStatus.Foreground = result.Success
                    ? new SolidColorBrush(Color.FromRgb(16, 124, 16))
                    : new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            catch (Exception ex)
            {
                TagDeployStatus.Text = $"Error: {ex.Message}";
                TagDeployStatus.Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            finally
            {
                TagDeployButton.IsEnabled = true;
            }
        }

        private void TagDeployCancelButton_Click(object sender, RoutedEventArgs e)
        {
            TagDeployPanel.Visibility = Visibility.Collapsed;
            TagDeployStatus.Text = string.Empty;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Policy tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulatePolicyTab()
        {
            if (_scanData.PolicyInventory != null)
            {
                PolicyCountText.Text = _scanData.PolicyInventory.TotalAssignments.ToString();
                PolicyNonCompliantText.Text = _scanData.PolicyInventory.TotalNonCompliant.ToString();

                int totalRes = _scanData.PolicyInventory.ComplianceBySubMap.Values.Sum(c => c.TotalResources);
                int compliant = _scanData.PolicyInventory.ComplianceBySubMap.Values.Sum(c => c.Compliant);
                PolicyComplianceText.Text = totalRes > 0
                    ? $"{(double)compliant / totalRes * 100:F0}%"
                    : "–";

                PolicyInventoryGrid.AutoGenerateColumns = true;
                PolicyInventoryGrid.ItemsSource = _scanData.PolicyInventory.Assignments
                    .Select(a => new
                    {
                        a.AssignmentName, a.Effect, a.EnforcementMode,
                        a.Origin, a.Subscription, a.Scope
                    })
                    .ToList();

                PolicyComplianceGrid.AutoGenerateColumns = true;
                PolicyComplianceGrid.ItemsSource = _scanData.PolicyInventory.ComplianceBySubMap.Values
                    .Select(c => new
                    {
                        c.Subscription, c.Compliant, c.NonCompliant, c.TotalResources,
                        CompliancePct = c.TotalResources > 0
                            ? $"{(double)c.Compliant / c.TotalResources * 100:F0}%"
                            : "N/A"
                    })
                    .ToList();
            }

            if (_scanData.PolicyRecs != null)
            {
                PolicyRecsCountText.Text = _scanData.PolicyRecs.Analysis.Count.ToString();
                PolicyRecsComplianceText.Text =
                    $"{_scanData.PolicyRecs.Assigned} of {_scanData.PolicyRecs.Analysis.Count} CAF policies assigned " +
                    $"({_scanData.PolicyRecs.CompliancePct:F0}% compliance)";
                PopulatePolicyRecsGrid();
            }
        }

        private void PopulatePolicyRecsGrid()
        {
            PolicyRecsGrid.Columns.Clear();
            PolicyRecsGrid.AutoGenerateColumns = false;

            PolicyRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Policy",
                Width = new DataGridLength(1.5, DataGridLengthUnitType.Star),
                Binding = new System.Windows.Data.Binding("DisplayName")
            });
            PolicyRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Status", Width = 95,
                Binding = new System.Windows.Data.Binding("Status")
            });
            PolicyRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Category", Width = 90,
                Binding = new System.Windows.Data.Binding("Category")
            });
            PolicyRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Priority", Width = 85,
                Binding = new System.Windows.Data.Binding("Priority")
            });
            PolicyRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Effect", Width = 80,
                Binding = new System.Windows.Data.Binding("Effect")
            });
            PolicyRecsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Purpose",
                Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                Binding = new System.Windows.Data.Binding("Purpose")
            });

            var actionCol = new DataGridTemplateColumn { Header = "Action", Width = 105 };
            var tmpl = new DataTemplate();
            var btnFactory = new FrameworkElementFactory(typeof(Button));
            btnFactory.SetValue(Button.PaddingProperty, new Thickness(6, 2, 6, 2));
            btnFactory.SetValue(Button.MarginProperty, new Thickness(2));
            btnFactory.SetValue(Button.FontSizeProperty, 11.0);
            btnFactory.SetValue(Button.CursorProperty, Cursors.Hand);
            // Label text based on Status
            var contentBinding = new System.Windows.Data.Binding("Status")
            {
                Converter = new StatusToButtonLabelConverter()
            };
            btnFactory.SetBinding(Button.ContentProperty, contentBinding);
            btnFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler(PolicyRecAction_Click));
            tmpl.VisualTree = btnFactory;
            actionCol.CellTemplate = tmpl;
            PolicyRecsGrid.Columns.Add(actionCol);

            PolicyRecsGrid.ItemsSource = _scanData.PolicyRecs?.Analysis ?? new List<PolicyRecItem>();
        }

        private void PolicyRecAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not PolicyRecItem item) return;

            if (item.Status == "Assigned")
            {
                var assignment = _scanData.PolicyInventory?.Assignments
                    .FirstOrDefault(a => a.PolicyDefId == item.PolicyDefId);
                ShowPolicyUnassignPanel(item.DisplayName, item.PolicyDefId,
                    assignment?.AssignmentId ?? string.Empty);
            }
            else
            {
                _ = ShowPolicyDeployPanelAsync(item.PolicyDefId, item.DisplayName,
                    item.AllowedEffects, item.DefaultEffect, item.Parameters);
            }
        }

        private async Task ShowPolicyDeployPanelAsync(
            string policyDefId, string displayName,
            List<string> allowedEffects, string defaultEffect,
            List<PolicyParam> parameters)
        {
            _currentPolicyDefId = policyDefId;
            _currentPolicyDisplayName = displayName;
            _policyUnassignMode = false;

            PolicyDeployPanel.Visibility = Visibility.Visible;
            PolicyDeployStatus.Text = string.Empty;
            PolicyDeployTitle.Text = $"Deploy Policy: {displayName}";
            PolicyDeployButton.Content = "Deploy Policy";
            PolicyRemediateButton.Visibility = Visibility.Collapsed;

            PolicyEffectSelector.Items.Clear();
            foreach (var eff in allowedEffects) PolicyEffectSelector.Items.Add(eff);
            if (allowedEffects.Contains(defaultEffect))
                PolicyEffectSelector.SelectedItem = defaultEffect;
            else if (PolicyEffectSelector.Items.Count > 0)
                PolicyEffectSelector.SelectedIndex = 0;

            PolicyParamsPanel.Children.Clear();
            foreach (var param in parameters)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row.Children.Add(new TextBlock
                {
                    Text = $"{param.Label}:",
                    Width = 200, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBox
                {
                    Name = $"Param_{param.Name}",
                    Width = 300, FontSize = 12, Text = param.DefaultValue,
                    ToolTip = param.Required ? "Required" : "Optional"
                });
                PolicyParamsPanel.Children.Add(row);
            }

            PolicyScopeSelector.Items.Clear();
            if (_policyDeployService != null && _scanData.Auth?.Subscriptions != null)
            {
                try
                {
                    var scopes = await _policyDeployService.GetPolicyScopesAsync(_scanData.Auth.Subscriptions);
                    foreach (var (dn, rid) in scopes)
                        PolicyScopeSelector.Items.Add(new ComboBoxItem { Content = dn, Tag = rid });
                    if (PolicyScopeSelector.Items.Count > 0) PolicyScopeSelector.SelectedIndex = 0;
                }
                catch { /* best-effort */ }
            }
        }

        private void ShowPolicyUnassignPanel(
            string policyDisplayName, string policyDefId, string assignmentId)
        {
            _currentPolicyDefId = policyDefId;
            _currentPolicyDisplayName = policyDisplayName;
            _currentPolicyAssignmentId = assignmentId;
            _policyUnassignMode = true;

            PolicyDeployPanel.Visibility = Visibility.Visible;
            PolicyDeployStatus.Text = string.Empty;
            PolicyDeployTitle.Text = $"Remove Policy Assignment: {policyDisplayName}";
            PolicyDeployButton.Content = "Remove Assignment";
            PolicyRemediateButton.Visibility = Visibility.Collapsed;
            PolicyParamsPanel.Children.Clear();
        }

        private async void PolicyDeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (_policyDeployService == null) { PolicyDeployStatus.Text = "Not connected."; return; }

            string scope = (PolicyScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(scope)) { PolicyDeployStatus.Text = "Select a scope."; return; }

            PolicyDeployButton.IsEnabled = false;
            PolicyDeployStatus.Text = _policyUnassignMode ? "Removing…" : "Deploying…";

            try
            {
                (bool Success, string Message) result;
                if (_policyUnassignMode)
                {
                    result = await _policyDeployService.RemovePolicyAssignmentAsync(_currentPolicyAssignmentId);
                }
                else
                {
                    string effect = PolicyEffectSelector.SelectedItem?.ToString() ?? "Audit";
                    var parameters = new Dictionary<string, string>();
                    foreach (UIElement child in PolicyParamsPanel.Children)
                    {
                        if (child is StackPanel row)
                        {
                            foreach (UIElement c2 in row.Children)
                            {
                                if (c2 is TextBox tb && tb.Name.StartsWith("Param_", StringComparison.Ordinal))
                                    parameters[tb.Name["Param_".Length..]] = tb.Text;
                            }
                        }
                    }
                    result = await _policyDeployService.DeployPolicyAsync(
                        scope, _currentPolicyDefId, effect, _currentPolicyDisplayName, parameters);

                    if (result.Success && (effect == "DeployIfNotExists" || effect == "Modify"))
                        PolicyRemediateButton.Visibility = Visibility.Visible;
                }

                PolicyDeployStatus.Text = result.Message;
                PolicyDeployStatus.Foreground = result.Success
                    ? new SolidColorBrush(Color.FromRgb(16, 124, 16))
                    : new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            catch (Exception ex)
            {
                PolicyDeployStatus.Text = $"Error: {ex.Message}";
                PolicyDeployStatus.Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            finally
            {
                PolicyDeployButton.IsEnabled = true;
                PolicyDeployButton.Content = _policyUnassignMode ? "Remove Assignment" : "Deploy Policy";
            }
        }

        private void PolicyDeployCancelButton_Click(object sender, RoutedEventArgs e)
        {
            PolicyDeployPanel.Visibility = Visibility.Collapsed;
            PolicyDeployStatus.Text = string.Empty;
            PolicyDeployButton.Content = "Deploy Policy";
        }

        private async void PolicyRemediateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_policyDeployService == null) return;
            string scope = (PolicyScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(scope)) { PolicyDeployStatus.Text = "Select a scope."; return; }

            PolicyRemediateButton.IsEnabled = false;
            PolicyDeployStatus.Text = "Creating remediation task…";

            try
            {
                var result = await _policyDeployService.CreateRemediationAsync(scope, _currentPolicyDefId);
                PolicyDeployStatus.Text = result.Message;
                PolicyDeployStatus.Foreground = result.Success
                    ? new SolidColorBrush(Color.FromRgb(16, 124, 16))
                    : new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            catch (Exception ex)
            {
                PolicyDeployStatus.Text = $"Error: {ex.Message}";
                PolicyDeployStatus.Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            finally
            {
                PolicyRemediateButton.IsEnabled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Optimization tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateOptimizationTab()
        {
            string sym = CurrencyHelper.GetSymbol(_scanData.Costs.Values.FirstOrDefault()?.Currency ?? "USD");

            // AHB
            if (_scanData.Ahb != null)
            {
                AHBCountText.Text = _scanData.Ahb.TotalOpportunities.ToString();
                AHBDetailText.Text =
                    $"Win VMs: {_scanData.Ahb.WindowsVMs.Count}  SQL VMs: {_scanData.Ahb.SqlVMs.Count}  SQL DBs: {_scanData.Ahb.SqlDatabases.Count}";
                AHBSummaryText.Text = _scanData.Ahb.Summary;

                AHBGrid.AutoGenerateColumns = true;
                AHBGrid.ItemsSource = _scanData.Ahb.WindowsVMs
                    .Select(v => new { Type = "Windows VM", v.Name, v.ResourceGroup, v.SubscriptionId, v.Location, v.VmSize, License = v.CurrentLicense })
                    .Cast<object>()
                    .Concat(_scanData.Ahb.SqlVMs
                        .Select(v => new { Type = "SQL VM", v.Name, v.ResourceGroup, v.SubscriptionId, v.Location, VmSize = v.VmSize, License = v.CurrentLicense }))
                    .Concat(_scanData.Ahb.SqlDatabases
                        .Select(v => new { Type = "SQL Database", v.Name, v.ResourceGroup, v.SubscriptionId, v.Location, VmSize = v.Sku, License = v.CurrentLicense }))
                    .ToList();
            }

            // Orphaned resources
            if (_scanData.OrphanedResources != null)
            {
                OrphanCountText.Text = _scanData.OrphanedResources.TotalCount.ToString();
                OrphanDetailText.Text = _scanData.OrphanedResources.Summary.Count > 0
                    ? string.Join(", ", _scanData.OrphanedResources.Summary.Select(s => $"{s.Category}: {s.Count}"))
                    : string.Empty;
                OrphanSummaryText.Text = $"{_scanData.OrphanedResources.TotalCount} orphaned resource(s) found.";
                OrphanGrid.AutoGenerateColumns = true;
                OrphanGrid.ItemsSource = _scanData.OrphanedResources.Orphans.Count > 0
                    ? _scanData.OrphanedResources.Orphans : null;
            }

            // Commitment utilization
            if (_scanData.Commitments != null)
            {
                RIUtilText.Text = _scanData.Commitments.TotalCount > 0
                    ? $"{_scanData.Commitments.AvgUtilization:F0}%" : "None";
                RIUtilDetail.Text = $"{_scanData.Commitments.TotalCount} commitment(s)";
                CommitmentGrid.AutoGenerateColumns = true;
                CommitmentGrid.ItemsSource = _scanData.Commitments.Commitments.Count > 0
                    ? _scanData.Commitments.Commitments : null;
            }

            // Advisor recommendations
            if (_scanData.Optimization != null)
            {
                AdvisorCountText.Text = _scanData.Optimization.TotalCount.ToString();
                AdvisorSavingsText.Text = _scanData.Optimization.EstimatedAnnualSavings > 0
                    ? $"Est. savings: {sym}{_scanData.Optimization.EstimatedAnnualSavings:N0}/yr"
                    : string.Empty;
                AdvisorGrid.AutoGenerateColumns = true;
                AdvisorGrid.ItemsSource = _scanData.Optimization.Recommendations.Count > 0
                    ? _scanData.Optimization.Recommendations : null;
            }

            // RI recommendations
            if (_scanData.Reservations != null)
            {
                RIGrid.AutoGenerateColumns = true;
                RIGrid.ItemsSource = _scanData.Reservations.ReservationRecommendations.Count > 0
                    ? _scanData.Reservations.ReservationRecommendations : null;

                string contractType = _scanData.Contract.Count > 0
                    ? _scanData.Contract[0].AgreementType : string.Empty;
                RIContractNote.Text = contractType == "EnterpriseAgreement"
                    ? "✓ Your EA agreement supports RI purchases directly in the Azure portal."
                    : contractType == "MicrosoftCustomerAgreement"
                        ? "✓ Your MCA agreement supports RI purchases."
                        : string.Empty;
            }

            // Savings plan (filter from advisor recs)
            SPGrid.AutoGenerateColumns = true;
            var spRecs = _scanData.Optimization?.Recommendations
                .Where(r => r.Category?.Contains("SavingsPlan", StringComparison.OrdinalIgnoreCase) == true
                         || r.Category?.Contains("Savings Plan", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            SPGrid.ItemsSource = spRecs?.Count > 0 ? spRecs : null;
            SPContractNote.Text = string.Empty;

            // Idle VMs
            if (_scanData.IdleVMs != null)
            {
                IdleVMSummaryText.Text =
                    $"{_scanData.IdleVMs.Count} idle/underutilized VM(s) of {_scanData.IdleVMs.ScannedVMs} scanned.";
                IdleVMGrid.AutoGenerateColumns = true;
                IdleVMGrid.ItemsSource = _scanData.IdleVMs.IdleVMs.Count > 0
                    ? _scanData.IdleVMs.IdleVMs : null;
            }

            // Storage tier
            if (_scanData.StorageTier != null)
            {
                StorageTierSummaryText.Text =
                    $"{_scanData.StorageTier.Count} storage account(s) may benefit from tier change.";
                StorageTierGrid.AutoGenerateColumns = true;
                StorageTierGrid.ItemsSource = _scanData.StorageTier.StorageAccounts.Count > 0
                    ? _scanData.StorageTier.StorageAccounts : null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Billing tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateBillingTab()
        {
            if (_scanData.Billing == null) return;

            BillingAccessNote.Text = _scanData.Billing.HasBillingAccess
                ? string.Empty
                : "Note: No billing account access. Requires 'Billing Reader' role on your billing account.";

            BillingAccountsGrid.AutoGenerateColumns = true;
            BillingAccountsGrid.ItemsSource = _scanData.Billing.BillingAccounts.Count > 0
                ? _scanData.Billing.BillingAccounts : null;

            BillingProfilesGrid.AutoGenerateColumns = true;
            BillingProfilesGrid.ItemsSource = _scanData.Billing.BillingProfiles.Count > 0
                ? _scanData.Billing.BillingProfiles : null;

            InvoiceSectionsGrid.AutoGenerateColumns = true;
            InvoiceSectionsGrid.ItemsSource = _scanData.Billing.InvoiceSections.Count > 0
                ? _scanData.Billing.InvoiceSections : null;

            if (_scanData.Billing.EADepartments.Count > 0)
            {
                EADeptHeader.Visibility = Visibility.Visible;
                EADeptGrid.Visibility = Visibility.Visible;
                EADeptGrid.AutoGenerateColumns = true;
                EADeptGrid.ItemsSource = _scanData.Billing.EADepartments
                    .Select(d => new { Department = d }).ToList();
            }

            CostAllocationGrid.AutoGenerateColumns = true;
            CostAllocationGrid.ItemsSource = _scanData.Billing.CostAllocationRules.Count > 0
                ? _scanData.Billing.CostAllocationRules.Select(r => new { Rule = r }).ToList()
                : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Budgets tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateBudgetsTab()
        {
            if (_scanData.Auth?.Subscriptions == null) return;

            // Subscription selector for budget detail view
            BudgetSubSelector.SelectionChanged -= BudgetSubSelector_SelectionChanged;
            BudgetSubSelector.Items.Clear();
            BudgetSubSelector.Items.Add("All Subscriptions");
            foreach (var sub in _scanData.Auth.Subscriptions)
                BudgetSubSelector.Items.Add(sub.Name);
            BudgetSubSelector.SelectedIndex = 0;
            BudgetSubSelector.SelectionChanged += BudgetSubSelector_SelectionChanged;

            // Populate deploy-budget scope selector
            BudgetDeployScopeSelector.Items.Clear();
            BudgetDeployScopeSelector.Items.Add(
                new ComboBoxItem { Content = "All Subscriptions", Tag = "all", IsSelected = true });
            foreach (var sub in _scanData.Auth.Subscriptions)
                BudgetDeployScopeSelector.Items.Add(
                    new ComboBoxItem { Content = sub.Name, Tag = sub.Id });
            if (BudgetDeployScopeSelector.Items.Count > 0)
                BudgetDeployScopeSelector.SelectedIndex = 0;

            // Budget policy scope
            BudgetPolicyScopeSelector.Items.Clear();
            foreach (var sub in _scanData.Auth.Subscriptions)
                BudgetPolicyScopeSelector.Items.Add(
                    new ComboBoxItem { Content = sub.Name, Tag = sub.Id });
            if (BudgetPolicyScopeSelector.Items.Count > 0)
                BudgetPolicyScopeSelector.SelectedIndex = 0;

            // Tag name selector for budget tag filter
            if (_scanData.Tags?.TagNames?.Count > 0)
            {
                BudgetDeployTagNameSelector.Items.Clear();
                BudgetDeployTagNameSelector.Items.Add(
                    new ComboBoxItem { Content = "(No tag filter)", Tag = string.Empty, IsSelected = true });
                foreach (var tag in _scanData.Tags.TagNames.Keys)
                    BudgetDeployTagNameSelector.Items.Add(new ComboBoxItem { Content = tag, Tag = tag });
                BudgetDeployTagNameSelector.SelectedIndex = 0;
            }

            UpdateBudgetDetailGrid(null);
        }

        private void BudgetSubSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = BudgetSubSelector.SelectedItem?.ToString() ?? "All Subscriptions";
            if (selected == "All Subscriptions")
                UpdateBudgetDetailGrid(null);
            else
            {
                var sub = _scanData.Auth?.Subscriptions.FirstOrDefault(s => s.Name == selected);
                UpdateBudgetDetailGrid(sub?.Id);
            }
        }

        private void UpdateBudgetDetailGrid(string? subscriptionId)
        {
            if (_scanData.Budgets == null) return;

            var budgets = subscriptionId == null
                ? _scanData.Budgets.Budgets
                : _scanData.Budgets.Budgets.Where(b => b.SubscriptionId == subscriptionId).ToList();

            BudgetSubSummary.Text = subscriptionId == null
                ? $"{_scanData.Budgets.TotalBudgets} budget(s) across {_scanData.Budgets.SubsWithBudget} subscription(s).  " +
                  $"Coverage: {_scanData.Budgets.BudgetCoverage:F0}%  " +
                  $"Over budget: {_scanData.Budgets.OverBudgetCount}  At risk: {_scanData.Budgets.AtRiskCount}"
                : $"{budgets.Count} budget(s) for the selected subscription.";

            BudgetDetailGrid.AutoGenerateColumns = true;
            BudgetDetailGrid.ItemsSource = budgets.Count > 0 ? budgets : null;
        }

        private async void BudgetDeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (_budgetService == null) { BudgetDeployStatus.Text = "Not connected."; return; }

            string budgetName = BudgetDeployNameInput.Text.Trim();
            string amountStr = BudgetDeployAmountInput.Text.Trim();
            string timeGrain = (BudgetDeployGrainSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monthly";
            string emailsStr = BudgetDeployEmailInput.Text.Trim();
            string scopeTag = (BudgetDeployScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(budgetName)) { BudgetDeployStatus.Text = "Enter a budget name."; return; }
            if (!double.TryParse(amountStr, out double amount) || amount <= 0)
            {
                BudgetDeployStatus.Text = "Enter a valid budget amount.";
                return;
            }

            var emails = emailsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var thresholds = new List<double>();
            foreach (var tb in new[] { BudgetThreshold1, BudgetThreshold2, BudgetThreshold3, BudgetThreshold4 })
            {
                if (double.TryParse(tb.Text.Trim(), out double t) && t > 0)
                    thresholds.Add(t);
            }
            if (thresholds.Count == 0) thresholds = new List<double> { 80, 100 };

            BudgetDeployButton.IsEnabled = false;
            BudgetDeployStatus.Text = "Deploying budget(s)…";
            BudgetDeployStatus.Foreground = SystemColors.ControlTextBrush;

            try
            {
                var targetSubs = (scopeTag == "all" || string.IsNullOrEmpty(scopeTag))
                    ? _scanData.Auth?.Subscriptions.Select(s => s.Id).ToList() ?? new List<string>()
                    : new List<string> { scopeTag };

                foreach (string subId in targetSubs)
                {
                    var result = await _budgetService.DeployBudgetAsync(
                        subId, budgetName, amount, timeGrain, emails, thresholds);
                    if (!result.Success)
                    {
                        BudgetDeployStatus.Text = result.Message;
                        BudgetDeployStatus.Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1));
                        return;
                    }
                }

                BudgetDeployStatus.Text = $"Budget '{budgetName}' deployed to {targetSubs.Count} subscription(s).";
                BudgetDeployStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 124, 16));
            }
            catch (Exception ex)
            {
                BudgetDeployStatus.Text = $"Error: {ex.Message}";
                BudgetDeployStatus.Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            finally
            {
                BudgetDeployButton.IsEnabled = true;
            }
        }

        private void BudgetDeployCancelButton_Click(object sender, RoutedEventArgs e)
        {
            BudgetDeployStatus.Text = string.Empty;
            BudgetDeployNameInput.Text = "default-budget";
            BudgetDeployAmountInput.Text = "1000";
        }

        private async void BudgetPolicyDeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (_policyDeployService == null) { BudgetPolicyStatus.Text = "Not connected."; return; }

            string effect = (BudgetPolicyEffectSelector.SelectedItem as ComboBoxItem)?.Content?.ToString()
                ?? "AuditIfNotExists";
            string scope = (BudgetPolicyScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                ?? string.Empty;

            if (string.IsNullOrEmpty(scope)) { BudgetPolicyStatus.Text = "Select a scope."; return; }

            BudgetPolicyDeployButton.IsEnabled = false;
            BudgetPolicyStatus.Text = "Deploying budget policy…";

            try
            {
                // Azure built-in: "Deploy a budget on subscriptions under your management group scope"
                // (DINE) or "Audit subscriptions missing a budget" (Audit)
                string policyDefId = effect == "DeployIfNotExists"
                    ? "/providers/Microsoft.Authorization/policyDefinitions/a9b35f6b-9ff3-4ab9-a009-80b1bc7b6e60"
                    : "/providers/Microsoft.Authorization/policyDefinitions/7c42d7c2-082d-4c6d-b5b5-e4e5f53e5e95";

                var result = await _policyDeployService.DeployPolicyAsync(
                    scope, policyDefId, effect, $"Budget Policy – {effect}");

                BudgetPolicyStatus.Text = result.Message;
                BudgetPolicyStatus.Foreground = result.Success
                    ? new SolidColorBrush(Color.FromRgb(16, 124, 16))
                    : new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            catch (Exception ex)
            {
                BudgetPolicyStatus.Text = $"Error: {ex.Message}";
                BudgetPolicyStatus.Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1));
            }
            finally
            {
                BudgetPolicyDeployButton.IsEnabled = true;
            }
        }

        private void BudgetPolicyCancelButton_Click(object sender, RoutedEventArgs e)
            => BudgetPolicyStatus.Text = string.Empty;

        // ─────────────────────────────────────────────────────────────────────
        // FinOps Guidance tab
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateGuidanceTab()
        {
            GuidanceScorePanel.Children.Clear();
            ActionPlanPanel.Children.Clear();
            UnderstandPanel.Children.Clear();
            QuantifyPanel.Children.Clear();
            OptimizePanel.Children.Clear();
            PersonasPanel.Children.Clear();

            string sym = CurrencyHelper.GetSymbol(_scanData.Costs.Values.FirstOrDefault()?.Currency ?? "USD");
            double totalActual = _scanData.Costs.Values.Sum(c => c.Actual);
            double estSavings = (_scanData.Optimization?.EstimatedAnnualSavings ?? 0)
                              + (_scanData.Reservations?.EstimatedAnnualSavings ?? 0);

            // Score cards row
            var scoreWrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
            scoreWrap.Children.Add(MakeScoreCard("Tag Coverage",
                _scanData.Tags != null ? $"{_scanData.Tags.TagCoverage:F0}%" : "–",
                _scanData.Tags?.TagCoverage >= 80 ? "#107C10" : "#D83B01"));
            scoreWrap.Children.Add(MakeScoreCard("MTD Spend", $"{sym}{totalActual:N0}", "#0078D4"));
            scoreWrap.Children.Add(MakeScoreCard("Budget Coverage",
                _scanData.Budgets != null ? $"{_scanData.Budgets.BudgetCoverage:F0}%" : "–",
                _scanData.Budgets?.BudgetCoverage >= 80 ? "#107C10" : "#D83B01"));
            scoreWrap.Children.Add(MakeScoreCard("Est. Savings/yr",
                estSavings > 0 ? $"{sym}{estSavings:N0}" : "–", "#107C10"));
            scoreWrap.Children.Add(MakeScoreCard("AHB Opportunities",
                (_scanData.Ahb?.TotalOpportunities ?? 0).ToString(),
                _scanData.Ahb?.TotalOpportunities > 0 ? "#D83B01" : "#107C10"));
            scoreWrap.Children.Add(MakeScoreCard("Orphaned Resources",
                (_scanData.OrphanedResources?.TotalCount ?? 0).ToString(),
                _scanData.OrphanedResources?.TotalCount > 0 ? "#D83B01" : "#107C10"));
            GuidanceScorePanel.Children.Add(scoreWrap);

            // Scan warnings banner
            if (_scanData.Warnings.Count > 0)
            {
                GuidanceScorePanel.Children.Add(new TextBlock
                {
                    Text = $"⚠ {_scanData.Warnings.Count} scan warning(s): " +
                           string.Join(";  ", _scanData.Warnings.Take(5)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(216, 59, 1)),
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            ActionPlanSubtitle.Text =
                $"Scan found {_scanData.Warnings.Count} warning(s). " +
                "The following prioritized actions will reduce your Azure spend.";

            // Action plan – sorted by impact
            if (_scanData.Ahb?.TotalOpportunities > 0)
                AddGuidanceItem(ActionPlanPanel, "🏷️", "Enable Azure Hybrid Benefit",
                    $"{_scanData.Ahb.TotalOpportunities} resource(s) eligible. Typical savings: 40-85%.", "#D83B01");
            if (_scanData.OrphanedResources?.TotalCount > 0)
                AddGuidanceItem(ActionPlanPanel, "🗑️", "Clean Up Orphaned Resources",
                    $"{_scanData.OrphanedResources.TotalCount} resource(s) incurring cost with no value.", "#D83B01");
            if (_scanData.Optimization?.TotalCount > 0)
                AddGuidanceItem(ActionPlanPanel, "⚡", "Act on Advisor Recommendations",
                    $"{_scanData.Optimization.TotalCount} recommendations. Est. {sym}{_scanData.Optimization.EstimatedAnnualSavings:N0}/yr.", "#D83B01");
            if (_scanData.Budgets?.SubsWithoutBudget > 0)
                AddGuidanceItem(ActionPlanPanel, "📋", "Create Missing Budgets",
                    $"{_scanData.Budgets.SubsWithoutBudget} subscription(s) have no budget.", "#8764B8");
            if (_scanData.Tags?.TagCoverage < 80)
                AddGuidanceItem(ActionPlanPanel, "🏷", "Improve Tag Coverage",
                    $"Current coverage: {_scanData.Tags?.TagCoverage:F0}%. Target: 80%+.", "#8764B8");

            // Understand pillar
            AddGuidanceItem(UnderstandPanel, "📊", "Tag Coverage",
                $"{(_scanData.Tags != null ? $"{_scanData.Tags.TagCoverage:F1}%" : "Unknown")} of resources are tagged. " +
                $"You have {_scanData.Tags?.TagCount ?? 0} unique tag keys. " +
                $"{_scanData.Tags?.UntaggedCount ?? 0} resource(s) are untagged.", "#0078D4");
            AddGuidanceItem(UnderstandPanel, "💰", $"Month-to-Date Spend: {sym}{totalActual:N2}",
                $"Forecasted total: {sym}{_scanData.Costs.Values.Sum(c => c.Forecast):N2}. " +
                $"Across {_scanData.Auth?.Subscriptions.Count ?? 0} subscription(s).", "#0078D4");
            if (_scanData.Billing?.HasBillingAccess == true)
                AddGuidanceItem(UnderstandPanel, "🏦",
                    $"Billing Accounts: {_scanData.Billing.BillingAccounts.Count}",
                    "Review your billing structure to ensure accurate cost allocation and chargeback.",
                    "#0078D4");

            // Quantify pillar
            double budgetCoverage = _scanData.Budgets?.BudgetCoverage ?? 0;
            AddGuidanceItem(QuantifyPanel, "📋", $"Budget Coverage: {budgetCoverage:F0}%",
                budgetCoverage < 100
                    ? $"{_scanData.Budgets?.SubsWithoutBudget ?? 0} subscription(s) have no budget. " +
                      "Deploy budgets with alert thresholds from the Budgets tab."
                    : "All subscriptions have budgets. Review thresholds and consider forecast alerts.",
                "#8764B8");
            if (_scanData.AnomalyAlerts?.TriggeredAlerts.Count > 0)
                AddGuidanceItem(QuantifyPanel, "⚠️",
                    $"{_scanData.AnomalyAlerts.TriggeredAlerts.Count} Cost Alert(s) Triggered",
                    "Investigate triggered alerts in the Cost Analysis tab.", "#D83B01");

            // Optimize pillar
            if (_scanData.Ahb?.TotalOpportunities > 0)
                AddGuidanceItem(OptimizePanel, "🏷️",
                    $"Azure Hybrid Benefit: {_scanData.Ahb.TotalOpportunities} {(_scanData.Ahb.TotalOpportunities == 1 ? "opportunity" : "opportunities")}",
                    "Use existing Windows Server and SQL Server licenses to save 40-85%.", "#107C10");
            if (_scanData.OrphanedResources?.TotalCount > 0)
                AddGuidanceItem(OptimizePanel, "🗑️",
                    $"Orphaned Resources: {_scanData.OrphanedResources.TotalCount}",
                    "Delete orphaned disks, unused IPs, empty App Service Plans, and stale snapshots.", "#107C10");
            if (_scanData.Reservations?.ReservationRecommendations.Count > 0)
                AddGuidanceItem(OptimizePanel, "💳",
                    $"Reserved Instance Recommendations: {_scanData.Reservations.ReservationRecommendations.Count}",
                    $"Purchase RIs for stable workloads. Est. {sym}{_scanData.Reservations.EstimatedAnnualSavings:N0}/yr.",
                    "#107C10");
            if (_scanData.Optimization?.TotalCount > 0)
                AddGuidanceItem(OptimizePanel, "⚡",
                    $"Advisor Recommendations: {_scanData.Optimization.TotalCount}",
                    $"Est. savings: {sym}{_scanData.Optimization.EstimatedAnnualSavings:N0}/yr.", "#107C10");

            // Personas
            AddGuidanceItem(PersonasPanel, "👤", "Cloud / FinOps Practitioner",
                "Owns day-to-day cost management, manages tagging policies, reviews anomalies, optimizes commitments.",
                "#0078D4");
            AddGuidanceItem(PersonasPanel, "👤", "Finance / CFO Office",
                "Reviews forecasts, approves budgets, requires chargeback reports by cost center or project.",
                "#8764B8");
            AddGuidanceItem(PersonasPanel, "👤", "Engineering Leads",
                "Responsible for right-sizing, AHB adoption, and acting on Advisor recommendations.",
                "#107C10");
            AddGuidanceItem(PersonasPanel, "👤", "Platform / Cloud Center of Excellence",
                "Sets governance policies, manages MG hierarchy, enforces tagging via Azure Policy.",
                "#D83B01");
        }

        private static Border MakeScoreCard(string title, string value, string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = title, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153))
            });
            sp.Children.Add(new TextBlock
            {
                Text = value, FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color), Margin = new Thickness(0, 4, 0, 0)
            });

            return new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14),
                Margin = new Thickness(6),
                Width = 160,
                Child = sp,
                Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 6, Opacity = 0.12, Direction = 270 }
            };
        }

        private static void AddGuidanceItem(
            StackPanel panel, string icon, string title, string detail, string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"{icon}  {title}", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap
            });
            sp.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });

            panel.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Child = sp
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Resources tab (static links – populated once at startup)
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateResourcesTab()
        {
            // FinOps Framework
            AddLink(ResourcesFinOpsPanel, "FinOps Framework",
                "https://www.finops.org/framework/",
                "The FinOps Foundation's open-source framework for cloud cost management practices.");
            AddLink(ResourcesFinOpsPanel, "Microsoft Cloud Adoption Framework – FinOps",
                "https://learn.microsoft.com/azure/cloud-adoption-framework/scenarios/finops/",
                "How to implement FinOps as part of your Azure cloud adoption journey.");
            AddLink(ResourcesFinOpsPanel, "Azure Well-Architected Framework – Cost Optimization",
                "https://learn.microsoft.com/azure/well-architected/cost-optimization/",
                "Design principles and recommendations for optimizing Azure workload costs.");

            // Cost Management
            AddLink(ResourcesCostPanel, "Azure Cost Management + Billing",
                "https://learn.microsoft.com/azure/cost-management-billing/",
                "Official documentation for Azure cost management, billing, and invoicing.");
            AddLink(ResourcesCostPanel, "Cost Allocation with Tags",
                "https://learn.microsoft.com/azure/cost-management-billing/costs/understand-work-scopes#tags",
                "How to use Azure tags to enable accurate cost allocation and chargeback.");
            AddLink(ResourcesCostPanel, "Azure Budgets",
                "https://learn.microsoft.com/azure/cost-management-billing/costs/tutorial-acm-create-budgets",
                "Create and manage budgets to monitor spending and receive alerts.");
            AddLink(ResourcesCostPanel, "Cost Anomaly Detection",
                "https://learn.microsoft.com/azure/cost-management-billing/understand/understand-azure-cost-anomalies",
                "Automated detection of unexpected cost spikes in your subscriptions.");

            // Rate Optimization
            AddLink(ResourcesRatePanel, "Azure Reservations",
                "https://learn.microsoft.com/azure/cost-management-billing/reservations/save-compute-costs-reservations",
                "Save up to 72% compared to pay-as-you-go with 1 or 3-year commitments.");
            AddLink(ResourcesRatePanel, "Azure Savings Plans",
                "https://learn.microsoft.com/azure/savings-plan/",
                "Flexible compute savings commitment that applies automatically across eligible usage.");
            AddLink(ResourcesRatePanel, "Azure Hybrid Benefit",
                "https://azure.microsoft.com/pricing/hybrid-benefit/",
                "Use existing Windows Server and SQL Server licenses to save 40-85% on Azure.");
            AddLink(ResourcesRatePanel, "Azure Spot VMs",
                "https://learn.microsoft.com/azure/virtual-machines/spot-vms",
                "Use Azure Spot VMs for interruptible workloads at up to 90% discount.");

            // Governance
            AddLink(ResourcesGovernancePanel, "Azure Policy",
                "https://learn.microsoft.com/azure/governance/policy/overview",
                "Enforce organizational standards and assess compliance at scale.");
            AddLink(ResourcesGovernancePanel, "Management Groups",
                "https://learn.microsoft.com/azure/governance/management-groups/overview",
                "Organize subscriptions into a hierarchy and apply governance at scale.");
            AddLink(ResourcesGovernancePanel, "CAF Tagging Strategy",
                "https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-tagging",
                "Best-practice tagging taxonomy for cost allocation, operations, and governance.");
            AddLink(ResourcesGovernancePanel, "Azure Resource Graph",
                "https://learn.microsoft.com/azure/governance/resource-graph/overview",
                "Query your Azure resource inventory at scale for reporting and compliance.");

            // Tools & Workbooks
            AddLink(ResourcesToolsPanel, "Azure FinOps Toolkit",
                "https://microsoft.github.io/finops-toolkit/",
                "Open-source collection of tools and templates for Azure cost optimization.");
            AddLink(ResourcesToolsPanel, "Cost Management Workbooks",
                "https://learn.microsoft.com/azure/cost-management-billing/costs/analyze-cost-data-azure-cost-management-workbooks",
                "Pre-built Azure Monitor Workbooks for cost analysis and optimization insights.");
            AddLink(ResourcesToolsPanel, "Azure Pricing Calculator",
                "https://azure.microsoft.com/pricing/calculator/",
                "Estimate costs for new Azure solutions before deploying.");
            AddLink(ResourcesToolsPanel, "Total Cost of Ownership Calculator",
                "https://azure.microsoft.com/pricing/tco/calculator/",
                "Compare on-premises costs to Azure cloud costs for migration planning.");
        }

        private static void AddLink(StackPanel panel, string title, string url, string description)
        {
            var hlText = new TextBlock { FontSize = 13 };
            var hl = new Hyperlink(new Run(title))
            {
                NavigateUri = new Uri(url),
                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212))
            };
            hl.RequestNavigate += (_, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
                        { UseShellExecute = true });
                }
                catch { /* best-effort */ }
                e.Handled = true;
            };
            hlText.Inlines.Add(hl);

            var sp = new StackPanel();
            sp.Children.Add(hlText);
            sp.Children.Add(new TextBlock
            {
                Text = description, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });

            panel.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(221, 221, 221)),
                BorderThickness = new Thickness(1),
                Child = sp
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Hierarchy tree
        // ─────────────────────────────────────────────────────────────────────
        private void PopulateHierarchyTree()
        {
            HierarchyTree.Items.Clear();
            if (_scanData.Hierarchy == null)
            {
                foreach (var sub in _scanData.Auth?.Subscriptions ?? new List<SubscriptionInfo>())
                    HierarchyTree.Items.Add(new TreeViewItem
                    {
                        Header = $"📋 {sub.Name}", ToolTip = sub.Id, FontSize = 12
                    });
                return;
            }

            if (_scanData.Hierarchy.RootGroup != null)
            {
                var rootItem = BuildTreeItem(_scanData.Hierarchy.RootGroup);
                HierarchyTree.Items.Add(rootItem);
                rootItem.IsExpanded = true;
            }
            else
            {
                foreach (var sub in _scanData.Auth?.Subscriptions ?? new List<SubscriptionInfo>())
                    HierarchyTree.Items.Add(new TreeViewItem
                    {
                        Header = $"📋 {sub.Name}", ToolTip = sub.Id, FontSize = 12
                    });
            }
        }

        private static TreeViewItem BuildTreeItem(ManagementGroup mg)
        {
            string icon = mg.Type?.Contains("subscriptions", StringComparison.OrdinalIgnoreCase) == true
                ? "📋" : "🏢";
            var item = new TreeViewItem
            {
                Header = $"{icon} {mg.DisplayName}",
                ToolTip = mg.Name,
                FontSize = 12,
                IsExpanded = true
            };
            foreach (var child in mg.Children)
                item.Items.Add(BuildTreeItem(child));
            return item;
        }
    }

    // ── Value converter: Status → button label ────────────────────────────────
    internal sealed class StatusToButtonLabelConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value?.ToString() == "Assigned" ? "Unassign" : "Deploy";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
