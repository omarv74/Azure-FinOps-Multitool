###########################################################################
# START-FIOPSMULTITOOL-CLI.PS1
# AZURE FINOPS MULTITOOL - HEADLESS / CLI LAUNCHER
###########################################################################
# Purpose: Cross-platform (Linux, macOS, Windows) headless launcher for
#          the Azure FinOps Multitool. Runs all scan modules and exports
#          results to CSV files without requiring a GUI or WPF.
#
# Author: Zac Larsen
# Date: Created for Linux/macOS and AVD headless environments
#
# Description:
# This launcher is a GUI-free alternative to Start-FinOpsMultitool.ps1.
# It reuses all the same scan modules but:
# 1. Replaces WPF/DispatcherFrame waits with Start-Sleep
# 2. Replaces WPF dialogs with console prompts
# 3. Authenticates using device code flow (works on any OS)
# 4. Exports results to a timestamped CSV folder automatically
#
# Prerequisites:
# - PowerShell 7.x (recommended for Linux/macOS)
# - Az modules: Az.Accounts, Az.Resources, Az.ResourceGraph,
#               Az.CostManagement, Az.Advisor, Az.Billing
#   Install-Module Az.Accounts, Az.Resources, Az.ResourceGraph,
#                  Az.CostManagement, Az.Advisor, Az.Billing -Scope CurrentUser
#
# Usage: pwsh ./Start-FinOpsMultitool-CLI.ps1
#        pwsh ./Start-FinOpsMultitool-CLI.ps1 -Environment AzureUSGovernment
#        pwsh ./Start-FinOpsMultitool-CLI.ps1 -OutputPath ~/finops-export
###########################################################################

#Requires -Version 5.1

param(
    [ValidateSet('AzureCloud', 'AzureUSGovernment')]
    [string]$Environment = 'AzureCloud',

    [string]$OutputPath = '',

    # Skip prompts and scan all subscriptions (useful for automation)
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Continue'
$script:ScriptRootDir  = $PSScriptRoot

# ─────────────────────────────────────────────────────────────────
# Verify required Az modules
# ─────────────────────────────────────────────────────────────────
$requiredModules = @('Az.Accounts', 'Az.Resources', 'Az.ResourceGraph', 'Az.CostManagement', 'Az.Advisor', 'Az.Billing')
$missing = @()
foreach ($mod in $requiredModules) {
    if (-not (Get-Module -ListAvailable -Name $mod)) { $missing += $mod }
}
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "Missing required modules: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "Run: Install-Module $($missing -join ', ') -Scope CurrentUser" -ForegroundColor Yellow
    exit 1
}

# ─────────────────────────────────────────────────────────────────
# Helper: Status output (replaces WPF status bar)
# ─────────────────────────────────────────────────────────────────
function Update-ScanStatus {
    param([string]$Message)
    Write-Host "  >> $Message" -ForegroundColor Cyan
}

# ─────────────────────────────────────────────────────────────────
# Helper: Plain access token (same as main script)
# ─────────────────────────────────────────────────────────────────
function Get-PlainAccessToken {
    param([string]$ResourceUrl = 'https://management.azure.com')
    $tok = (Get-AzAccessToken -ResourceUrl $ResourceUrl).Token
    if ($tok -is [securestring]) {
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($tok)
        try   { [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
        finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    } else { $tok }
}

# ─────────────────────────────────────────────────────────────────
# Helper: MG-scope state (same contract as main script)
# ─────────────────────────────────────────────────────────────────
$script:MgCostScopeFailed = $false

function Test-MgCostScope  { return (-not $script:MgCostScopeFailed) }
function Set-MgCostScopeFailed {
    $script:MgCostScopeFailed = $true
    Write-Host "  MG-scope cost access unavailable - falling back to per-subscription queries" -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────
# Helper: Invoke-AzRestMethodWithRetry (headless — no DispatcherFrame)
# ─────────────────────────────────────────────────────────────────
function Invoke-AzRestMethodWithRetry {
    param(
        [string]$Path,
        [string]$Method      = 'POST',
        [string]$Payload,
        [int]$MaxRetries     = 3,
        [int]$TimeoutSeconds = 60
    )
    for ($attempt = 0; $attempt -le $MaxRetries; $attempt++) {
        $rs = [runspacefactory]::CreateRunspace()
        $rs.Open()
        $ps = [powershell]::Create()
        $ps.Runspace = $rs
        [void]$ps.AddScript({
            param($p, $m, $pl)
            $params = @{ Path = $p; Method = $m; ErrorAction = 'Stop' }
            if ($pl) { $params['Payload'] = $pl }
            $r = Invoke-AzRestMethod @params
            $hdrs = @{}
            if ($r.Headers) { foreach ($k in $r.Headers.Keys) { $hdrs[$k] = $r.Headers[$k] } }
            [PSCustomObject]@{ StatusCode = $r.StatusCode; Content = $r.Content; Headers = $hdrs }
        }).AddArgument($Path).AddArgument($Method).AddArgument($Payload)

        $asyncResult = $ps.BeginInvoke()
        $deadline    = (Get-Date).AddSeconds($TimeoutSeconds)

        # Headless wait — no DispatcherFrame needed outside WPF
        while (-not $asyncResult.IsCompleted -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }

        $resp = $null
        if ($asyncResult.IsCompleted) {
            try {
                $raw  = $ps.EndInvoke($asyncResult)
                $resp = if ($raw -and $raw.Count -gt 0) { $raw[0] } else { $null }
            } catch {
                $ps.Dispose(); $rs.Close()
                throw
            }
        } else {
            $ps.Stop()
            Write-Warning "  REST call timed out after $($TimeoutSeconds)s: $Method $Path"
            $ps.Dispose(); $rs.Close()
            return [PSCustomObject]@{ StatusCode = 408; Content = '{"error":{"message":"Request timed out"}}'; Headers = @{} }
        }

        $ps.Dispose(); $rs.Close()

        if (-not $resp -or $resp.StatusCode -ne 429) { return $resp }

        $retryAfter = 10
        if ($resp.Headers -and $resp.Headers['Retry-After']) {
            $parsed = 0
            if ([int]::TryParse($resp.Headers['Retry-After'], [ref]$parsed)) { $retryAfter = [math]::Max($parsed, 5) }
        } else {
            $retryAfter = [math]::Min(10 * [math]::Pow(2, $attempt), 60)
        }
        Write-Host "  [429 Throttled] Waiting $($retryAfter)s before retry ($($attempt+1)/$MaxRetries)..." -ForegroundColor Yellow
        Start-Sleep -Seconds $retryAfter
    }
    return $resp
}

# ─────────────────────────────────────────────────────────────────
# Helper: Search-AzGraphSafe (headless — no DispatcherFrame)
# ─────────────────────────────────────────────────────────────────
function Search-AzGraphSafe {
    param(
        [Parameter(Mandatory)][string]$Query,
        [string[]]$Subscription,
        [int]$First          = 1000,
        [string]$SkipToken,
        [int]$TimeoutSeconds = 60,
        [int]$MaxRetries     = 2
    )
    for ($attempt = 0; $attempt -le $MaxRetries; $attempt++) {
        $rs = [runspacefactory]::CreateRunspace()
        $rs.Open()
        $ps = [powershell]::Create()
        $ps.Runspace = $rs
        [void]$ps.AddScript({
            param($q, $s, $f, $st)
            $p = @{ Query = $q; Subscription = $s; First = $f; ErrorAction = 'Stop' }
            if ($st) { $p['SkipToken'] = $st }
            $r    = Search-AzGraph @p
            $json = if ($r.Data -and $r.Data.Count -gt 0) {
                $r.Data | ConvertTo-Json -Depth 20 -Compress
            } else { '[]' }
            [PSCustomObject]@{ JsonData = $json; SkipToken = $r.SkipToken; Count = if ($r.Data) { $r.Data.Count } else { 0 } }
        }).AddArgument($Query).AddArgument($Subscription).AddArgument($First).AddArgument($SkipToken)

        $asyncResult = $ps.BeginInvoke()
        $deadline    = (Get-Date).AddSeconds($TimeoutSeconds)

        # Headless wait
        while (-not $asyncResult.IsCompleted -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }

        $result = $null
        $is429  = $false
        if ($asyncResult.IsCompleted) {
            try {
                $raw     = $ps.EndInvoke($asyncResult)
                $wrapper = if ($raw -and $raw.Count -gt 0) { $raw[0] } else { $null }
                if ($wrapper) {
                    $data = if ($wrapper.JsonData -and $wrapper.JsonData -ne '[]') {
                        $parsed = $wrapper.JsonData | ConvertFrom-Json
                        if ($parsed -is [array]) { $parsed } else { @($parsed) }
                    } else { @() }
                    $result = [PSCustomObject]@{ Data = $data; SkipToken = $wrapper.SkipToken; Count = $wrapper.Count }
                }
                if ($ps.Streams.Error.Count -gt 0) {
                    $errMsg = $ps.Streams.Error[0].Exception.Message
                    if ($errMsg -match '429|throttl|Too Many Requests') { $is429 = $true; $result = $null }
                    elseif (-not $result) { throw $ps.Streams.Error[0].Exception }
                }
            } catch {
                if ($_.Exception.Message -match '429|throttl|Too Many Requests') { $is429 = $true }
                else { $ps.Dispose(); $rs.Close(); throw }
            }
        } else {
            $ps.Stop()
            Write-Warning "  Resource Graph query timed out after $($TimeoutSeconds)s"
        }

        $ps.Dispose(); $rs.Close()

        if (-not $is429) { return $result }

        $retryAfter = [math]::Min(10 * [math]::Pow(2, $attempt), 30)
        Write-Host "  [429 Throttled - Resource Graph] Waiting $($retryAfter)s before retry ($($attempt+1)/$MaxRetries)..." -ForegroundColor Yellow
        Start-Sleep -Seconds $retryAfter
    }
    return $null
}

# ─────────────────────────────────────────────────────────────────
# Dot-source scan modules (same set as main script, skip Initialize-Scanner)
# ─────────────────────────────────────────────────────────────────
$modulePath = Join-Path $PSScriptRoot 'modules'

$scanModules = @(
    'Get-TenantHierarchy.ps1',
    'Get-ContractInfo.ps1',
    'Get-CostData.ps1',
    'Get-ResourceCosts.ps1',
    'Get-TagInventory.ps1',
    'Get-CostByTag.ps1',
    'Get-AHBOpportunities.ps1',
    'Get-ReservationAdvice.ps1',
    'Get-OptimizationAdvice.ps1',
    'Get-TagRecommendations.ps1',
    'Get-CostTrend.ps1',
    'Get-BillingStructure.ps1',
    'Get-CommitmentUtilization.ps1',
    'Get-OrphanedResources.ps1',
    'Get-BudgetStatus.ps1',
    'Get-SavingsRealized.ps1',
    'Get-PolicyInventory.ps1',
    'Get-PolicyRecommendations.ps1',
    'Get-StorageTierAdvice.ps1',
    'Get-IdleVMs.ps1'
)

foreach ($mod in $scanModules) {
    $modFile = Join-Path $modulePath $mod
    if (Test-Path $modFile) {
        . $modFile
    } else {
        Write-Warning "Module not found: $modFile"
    }
}

# ─────────────────────────────────────────────────────────────────
# Authentication — device code flow (works on any OS)
# ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  Azure FinOps Multitool - Headless CLI" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Authenticating to Azure ($Environment)..." -ForegroundColor White
Write-Host "A device code login prompt will appear below." -ForegroundColor Gray
Write-Host ""

try {
    $ctx = Get-AzContext -ErrorAction SilentlyContinue
    if (-not $ctx -or ($ctx.Environment.Name -ne $Environment)) {
        Connect-AzAccount -Environment $Environment -UseDeviceAuthentication -ErrorAction Stop | Out-Null
        $ctx = Get-AzContext
    } else {
        Write-Host "  Using existing session: $($ctx.Account.Id)" -ForegroundColor Green
    }
} catch {
    Write-Host "Authentication failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ─────────────────────────────────────────────────────────────────
# Tenant selection
# ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Loading accessible tenants..." -ForegroundColor White
$tenants = @(Get-AzTenant -ErrorAction SilentlyContinue)

$selectedTenantId = $null

if ($tenants.Count -eq 0) {
    Write-Host "No accessible tenants found." -ForegroundColor Red
    exit 1
} elseif ($tenants.Count -eq 1 -or $NonInteractive) {
    $selectedTenantId = $tenants[0].TenantId
    Write-Host "  Using tenant: $($tenants[0].Name) ($selectedTenantId)" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Available tenants:" -ForegroundColor White
    for ($i = 0; $i -lt $tenants.Count; $i++) {
        $t = $tenants[$i]
        $name = if ($t.Name -and $t.Name -ne $t.TenantId) { $t.Name } else { $t.TenantId }
        Write-Host "  [$($i+1)] $name  ($($t.TenantId))" -ForegroundColor Yellow
    }
    Write-Host ""
    $pick = Read-Host "Select tenant [1-$($tenants.Count)]"
    $pickIdx = 0
    if ([int]::TryParse($pick, [ref]$pickIdx) -and $pickIdx -ge 1 -and $pickIdx -le $tenants.Count) {
        $selectedTenantId = $tenants[$pickIdx - 1].TenantId
    } else {
        Write-Host "Invalid selection." -ForegroundColor Red
        exit 1
    }
}

# Switch tenant context if needed
if ($ctx.Tenant.Id -ne $selectedTenantId) {
    Write-Host "  Switching to tenant $selectedTenantId..." -ForegroundColor White
    Connect-AzAccount -Environment $Environment -TenantId $selectedTenantId -UseDeviceAuthentication -ErrorAction Stop | Out-Null
}

# ─────────────────────────────────────────────────────────────────
# Subscription selection
# ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Loading subscriptions..." -ForegroundColor White
$allSubs = @(Get-AzSubscription -TenantId $selectedTenantId -ErrorAction Stop |
    Where-Object { $_.State -eq 'Enabled' } | Sort-Object Name)

if ($allSubs.Count -eq 0) {
    Write-Host "No enabled subscriptions found in this tenant." -ForegroundColor Red
    exit 1
}

$selectedSubs = $allSubs

if (-not $NonInteractive) {
    Write-Host ""
    Write-Host "Found $($allSubs.Count) subscription(s):" -ForegroundColor White
    for ($i = 0; $i -lt $allSubs.Count; $i++) {
        Write-Host "  [$($i+1)] $($allSubs[$i].Name)  ($($allSubs[$i].Id))" -ForegroundColor Yellow
    }
    Write-Host "  [A] All subscriptions" -ForegroundColor Yellow
    Write-Host ""
    $pick = Read-Host "Select subscriptions (comma-separated numbers, or A for all)"

    if ($pick -match '^[Aa]$') {
        $selectedSubs = $allSubs
    } else {
        $indices = $pick -split ',' | ForEach-Object { $_.Trim() }
        $selectedSubs = @()
        foreach ($idx in $indices) {
            $n = 0
            if ([int]::TryParse($idx, [ref]$n) -and $n -ge 1 -and $n -le $allSubs.Count) {
                $selectedSubs += $allSubs[$n - 1]
            }
        }
        if ($selectedSubs.Count -eq 0) {
            Write-Host "No valid subscriptions selected. Using all." -ForegroundColor Yellow
            $selectedSubs = $allSubs
        }
    }
}

Write-Host ""
Write-Host "Scanning $($selectedSubs.Count) subscription(s):" -ForegroundColor Green
$selectedSubs | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Gray }
Write-Host ""

# ─────────────────────────────────────────────────────────────────
# Build auth object (matches Initialize-Scanner output contract)
# ─────────────────────────────────────────────────────────────────
$ctx = Get-AzContext
$authObj = [PSCustomObject]@{
    TenantId      = $selectedTenantId
    AccountName   = $ctx.Account.Id
    Environment   = $Environment
    Subscriptions = $selectedSubs
    SkippedSubs   = @()
    TenantSize    = if ($allSubs.Count -gt 100) { 'Large' } elseif ($allSubs.Count -gt 20) { 'Medium' } else { 'Small' }
}

# ─────────────────────────────────────────────────────────────────
# Run scan modules
# ─────────────────────────────────────────────────────────────────
$script:scanData = @{
    Auth          = $authObj
    Hierarchy     = $null
    Contract      = $null
    Costs         = $null
    ResourceCosts = $null
    Tags          = $null
    CostByTag     = $null
    CostTrend     = $null
    AHB           = $null
    Commitments   = $null
    Orphans       = $null
    IdleVMs       = $null
    StorageTier   = $null
    Reservations  = $null
    Optimization  = $null
    Budgets       = $null
    Savings       = $null
    TagRecs       = $null
    PolicyInv     = $null
    PolicyRecs    = $null
    Billing       = $null
}

$steps = @(
    @{ Name = 'Tenant Hierarchy';    Script = { $script:scanData.Hierarchy     = Get-TenantHierarchy         -TenantId $authObj.TenantId -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Contract Info';       Script = { $script:scanData.Contract      = Get-ContractInfo             -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Cost Data';           Script = { $script:scanData.Costs         = Get-CostData                 -TenantId $authObj.TenantId -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Resource Costs';      Script = { $script:scanData.ResourceCosts = Get-ResourceCosts            -TenantId $authObj.TenantId -Subscriptions $authObj.Subscriptions -CostData $script:scanData.Costs } },
    @{ Name = 'Tag Inventory';       Script = { $script:scanData.Tags          = Get-TagInventory             -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Cost By Tag';         Script = {
        $tagNames = if ($script:scanData.Tags) { $script:scanData.Tags.TagNames } else { @{} }
        $script:scanData.CostByTag = Get-CostByTag -TenantId $authObj.TenantId -ExistingTags $tagNames -Subscriptions $authObj.Subscriptions
    }},
    @{ Name = 'Cost Trend';          Script = { $script:scanData.CostTrend     = Get-CostTrend                -TenantId $authObj.TenantId -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'AHB Opportunities';   Script = { $script:scanData.AHB           = Get-AHBOpportunities        -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Commitment Utilization'; Script = {
        $agreementType = if ($script:scanData.Contract -and $script:scanData.Contract[0].AgreementType) { $script:scanData.Contract[0].AgreementType } else { '' }
        $script:scanData.Commitments = Get-CommitmentUtilization -Subscriptions $authObj.Subscriptions -AgreementType $agreementType
    }},
    @{ Name = 'Orphaned Resources';  Script = { $script:scanData.Orphans       = Get-OrphanedResources       -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Idle VMs';            Script = { $script:scanData.IdleVMs       = Get-IdleVMs                 -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Storage Tier Advice'; Script = { $script:scanData.StorageTier   = Get-StorageTierAdvice       -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Reservation Advice';  Script = { $script:scanData.Reservations  = Get-ReservationAdvice       -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Optimization Advice'; Script = { $script:scanData.Optimization  = Get-OptimizationAdvice      -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Budget Status';       Script = { $script:scanData.Budgets       = Get-BudgetStatus            -Subscriptions $authObj.Subscriptions -CostData $script:scanData.Costs } },
    @{ Name = 'Savings Realized';    Script = { $script:scanData.Savings       = Get-SavingsRealized         -TenantId $authObj.TenantId -Subscriptions $authObj.Subscriptions -CommitmentData $script:scanData.Commitments } },
    @{ Name = 'Tag Recommendations'; Script = {
        $tagNames = if ($script:scanData.Tags) { $script:scanData.Tags.TagNames } else { @{} }
        $tagLocs  = if ($script:scanData.Tags) { $script:scanData.Tags.TagLocations } else { @{} }
        $script:scanData.TagRecs = Get-TagRecommendations -ExistingTags $tagNames -TagLocations $tagLocs
    }},
    @{ Name = 'Policy Inventory';    Script = { $script:scanData.PolicyInv     = Get-PolicyInventory         -TenantId $authObj.TenantId -Subscriptions $authObj.Subscriptions } },
    @{ Name = 'Policy Recommendations'; Script = {
        $assignments = if ($script:scanData.PolicyInv) { $script:scanData.PolicyInv.Assignments } else { @() }
        $script:scanData.PolicyRecs = Get-PolicyRecommendations -ExistingAssignments $assignments
    }},
    @{ Name = 'Billing Structure';   Script = { $script:scanData.Billing       = Get-BillingStructure        -Subscriptions $authObj.Subscriptions } }
)

$total   = $steps.Count
$current = 0

foreach ($step in $steps) {
    $current++
    $pct = [math]::Round(($current / $total) * 100)
    Write-Host "[$pct%] $($step.Name)..." -ForegroundColor White
    try {
        & $step.Script
        Write-Host "      Done" -ForegroundColor Green
    } catch {
        Write-Host "      Warning: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ─────────────────────────────────────────────────────────────────
# Export to CSV
# ─────────────────────────────────────────────────────────────────
$stamp     = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$exportDir = if ($OutputPath) {
    Join-Path $OutputPath "FinOps-CLI-$stamp"
} else {
    Join-Path $PSScriptRoot "output\FinOps-CLI-$stamp"
}
New-Item -Path $exportDir -ItemType Directory -Force | Out-Null

Write-Host ""
Write-Host "Exporting results to: $exportDir" -ForegroundColor Cyan

$d = $script:scanData

# Helper
function Export-IfData {
    param([string]$File, [object[]]$Rows)
    if ($Rows -and $Rows.Count -gt 0) {
        $Rows | Export-Csv -Path (Join-Path $exportDir $File) -NoTypeInformation -Encoding UTF8
        Write-Host "  Exported: $File ($($Rows.Count) rows)" -ForegroundColor Gray
    }
}

# Subscription Costs
$subRows = @()
foreach ($sub in $d.Auth.Subscriptions) {
    $c = if ($d.Costs -and $d.Costs.ContainsKey($sub.Id)) { $d.Costs[$sub.Id] } else { @{ Actual = 0; Forecast = 0; Currency = 'USD' } }
    $subRows += [PSCustomObject]@{
        Subscription   = $sub.Name
        SubscriptionId = $sub.Id
        ActualMTD      = [math]::Round($c.Actual, 2)
        Forecast       = [math]::Round($c.Forecast, 2)
        Currency       = $c.Currency
    }
}
Export-IfData 'SubscriptionCosts.csv' $subRows

# Resource Costs
if ($d.ResourceCosts -and $d.ResourceCosts.Count -gt 0) {
    Export-IfData 'ResourceCosts.csv' ($d.ResourceCosts | ForEach-Object {
        [PSCustomObject]@{
            Subscription  = $_.Subscription
            ResourceGroup = $_.ResourceGroup
            ResourceType  = $_.ResourceType
            ResourcePath  = $_.ResourcePath
            ActualMTD     = [math]::Round($_.Actual, 2)
            Forecast      = [math]::Round($_.Forecast, 2)
            Currency      = $_.Currency
        }
    })
}

# Tag Inventory
if ($d.Tags -and $d.Tags.TagNames) {
    $tagRows = @()
    foreach ($tn in $d.Tags.TagNames.Keys) {
        $info = $d.Tags.TagNames[$tn]
        foreach ($v in $info.Values) {
            $tagRows += [PSCustomObject]@{
                TagName       = $tn
                TagValue      = $v.Value
                ResourceCount = $v.ResourceCount
            }
        }
    }
    Export-IfData 'TagInventory.csv' $tagRows
}

# Orphaned Resources
if ($d.Orphans -and $d.Orphans.Resources) {
    Export-IfData 'OrphanedResources.csv' ($d.Orphans.Resources | ForEach-Object {
        [PSCustomObject]@{
            Name           = $_.Name
            Type           = $_.Type
            ResourceGroup  = $_.ResourceGroup
            Subscription   = $_.Subscription
            Reason         = $_.Reason
            EstimatedSaving = if ($_.EstimatedSaving) { [math]::Round($_.EstimatedSaving, 2) } else { 0 }
        }
    })
}

# Idle VMs
if ($d.IdleVMs -and $d.IdleVMs.Count -gt 0) {
    Export-IfData 'IdleVMs.csv' ($d.IdleVMs | ForEach-Object {
        [PSCustomObject]@{
            Name           = $_.Name
            ResourceGroup  = $_.ResourceGroup
            Subscription   = $_.Subscription
            Size           = $_.Size
            AvgCPU         = $_.AvgCPU
            EstimatedSaving = if ($_.EstimatedSaving) { [math]::Round($_.EstimatedSaving, 2) } else { 0 }
        }
    })
}

# AHB Opportunities
if ($d.AHB -and $d.AHB.Opportunities) {
    Export-IfData 'AHBOpportunities.csv' ($d.AHB.Opportunities | ForEach-Object {
        [PSCustomObject]@{
            Name           = $_.Name
            Type           = $_.Type
            ResourceGroup  = $_.ResourceGroup
            Subscription   = $_.Subscription
            CurrentLicense = $_.CurrentLicense
            EstimatedSaving = if ($_.EstimatedSaving) { [math]::Round($_.EstimatedSaving, 2) } else { 0 }
        }
    })
}

# Budget Status
if ($d.Budgets -and $d.Budgets.Count -gt 0) {
    Export-IfData 'BudgetStatus.csv' ($d.Budgets | ForEach-Object {
        [PSCustomObject]@{
            BudgetName   = $_.BudgetName
            Subscription = $_.Subscription
            Amount       = $_.Amount
            ActualSpend  = [math]::Round($_.ActualSpend, 2)
            ForecastSpend = [math]::Round($_.ForecastSpend, 2)
            PercentUsed  = $_.PercentUsed
            Status       = $_.Status
        }
    })
}

# Policy Inventory
if ($d.PolicyInv -and $d.PolicyInv.Assignments) {
    Export-IfData 'PolicyAssignments.csv' ($d.PolicyInv.Assignments | ForEach-Object {
        [PSCustomObject]@{
            DisplayName       = $_.DisplayName
            Scope             = $_.Scope
            ComplianceState   = $_.ComplianceState
            NonCompliantCount = $_.NonCompliantCount
            Effect            = $_.Effect
        }
    })
}

# Storage Tier Advice
if ($d.StorageTier -and $d.StorageTier.Count -gt 0) {
    Export-IfData 'StorageTierAdvice.csv' ($d.StorageTier | ForEach-Object {
        [PSCustomObject]@{
            Name            = $_.Name
            ResourceGroup   = $_.ResourceGroup
            Subscription    = $_.Subscription
            CurrentTier     = $_.CurrentTier
            RecommendedTier = $_.RecommendedTier
            EstimatedSaving = if ($_.EstimatedSaving) { [math]::Round($_.EstimatedSaving, 2) } else { 0 }
        }
    })
}

# Savings Realized
if ($d.Savings) {
    $savingsRow = [PSCustomObject]@{
        TotalSavings     = if ($d.Savings.TotalSavings)     { [math]::Round($d.Savings.TotalSavings, 2) }     else { 0 }
        RISavings        = if ($d.Savings.RISavings)        { [math]::Round($d.Savings.RISavings, 2) }        else { 0 }
        SavingsPlanSavings = if ($d.Savings.SavingsPlanSavings) { [math]::Round($d.Savings.SavingsPlanSavings, 2) } else { 0 }
        AHBSavings       = if ($d.Savings.AHBSavings)       { [math]::Round($d.Savings.AHBSavings, 2) }       else { 0 }
    }
    Export-IfData 'SavingsRealized.csv' @($savingsRow)
}

# ─────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  Scan complete!" -ForegroundColor Green
Write-Host "  Results exported to:" -ForegroundColor Green
Write-Host "  $exportDir" -ForegroundColor White
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
