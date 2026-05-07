using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Helpers
{
    /// <summary>
    /// Exports ScanData to CSV, JSON, or HTML report formats.
    /// </summary>
    public static class ExportHelper
    {
        // ── JSON export ───────────────────────────────────────────────────────

        public static void ExportToJson(ScanData data, string path)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented       = true,
                PropertyNamingPolicy = null
            };
            var json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        // ── CSV export (multi-sheet as multiple files or tabs) ────────────────

        public static void ExportToCsv(ScanData data, string path)
        {
            // Export into a ZIP-friendly folder structure or concatenated CSV
            // For simplicity: write the most important table (costs) as CSV
            string dir = Path.GetDirectoryName(path) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(path);

            // Subscription Costs
            WriteCsv(Path.Combine(dir, $"{baseName}_costs.csv"),
                GetCostRows(data));

            // Resource Costs
            if (data.ResourceCosts?.Count > 0)
                WriteCsv(Path.Combine(dir, $"{baseName}_resource_costs.csv"),
                    GetResourceCostRows(data));

            // Tag Inventory
            if (data.Tags?.TagNames?.Count > 0)
                WriteCsv(Path.Combine(dir, $"{baseName}_tags.csv"),
                    GetTagRows(data));

            // AHB Opportunities
            if (data.Ahb != null)
                WriteCsv(Path.Combine(dir, $"{baseName}_ahb.csv"),
                    GetAhbRows(data));

            // Optimization Recommendations
            if (data.Optimization?.Recommendations?.Count > 0)
                WriteCsv(Path.Combine(dir, $"{baseName}_optimization.csv"),
                    GetOptRows(data));

            // Orphaned Resources
            if (data.OrphanedResources?.Orphans?.Count > 0)
                WriteCsv(Path.Combine(dir, $"{baseName}_orphans.csv"),
                    GetOrphanRows(data));

            // Budgets
            if (data.Budgets?.Budgets?.Count > 0)
                WriteCsv(Path.Combine(dir, $"{baseName}_budgets.csv"),
                    GetBudgetRows(data));
        }

        private static void WriteCsv(string path, IEnumerable<Dictionary<string, string>> rows)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true
            };

            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            using var csv = new CsvWriter(writer, config);

            bool headerWritten = false;
            foreach (var row in rows)
            {
                if (!headerWritten)
                {
                    foreach (var key in row.Keys)
                        csv.WriteField(key);
                    csv.NextRecord();
                    headerWritten = true;
                }
                foreach (var val in row.Values)
                    csv.WriteField(val);
                csv.NextRecord();
            }
        }

        // ── HTML export ───────────────────────────────────────────────────────

        public static void ExportToHtml(ScanData data, string path)
        {
            var sb = new StringBuilder();
            string tenantId = data.Auth?.TenantId ?? "Unknown";
            string scanDate = (data.ScanCompleted ?? data.ScanStarted).ToString("yyyy-MM-dd HH:mm:ss UTC");

            sb.AppendLine(HtmlHeader($"Azure FinOps Report – {tenantId}"));
            sb.AppendLine("<body>");
            sb.AppendLine($"<h1>Azure FinOps Report</h1>");
            sb.AppendLine($"<p><strong>Tenant:</strong> {tenantId} &nbsp; <strong>Generated:</strong> {scanDate}</p>");

            // Summary section
            sb.AppendLine("<h2>Executive Summary</h2><table>");
            sb.AppendLine(HtmlTr("Subscriptions", (data.Auth?.Subscriptions?.Count ?? 0).ToString()));
            if (data.Costs?.Count > 0)
            {
                double totalActual = 0;
                foreach (var c in data.Costs.Values) totalActual += c.Actual;
                sb.AppendLine(HtmlTr("Total MTD Spend", $"${totalActual:N2}"));
            }
            if (data.Tags != null)
                sb.AppendLine(HtmlTr("Tag Coverage", $"{data.Tags.TagCoverage}%"));
            if (data.Ahb != null)
                sb.AppendLine(HtmlTr("AHB Opportunities", data.Ahb.TotalOpportunities.ToString()));
            if (data.OrphanedResources != null)
                sb.AppendLine(HtmlTr("Orphaned Resources", data.OrphanedResources.TotalCount.ToString()));
            sb.AppendLine("</table>");

            // Cost section
            if (data.Costs?.Count > 0)
            {
                sb.AppendLine("<h2>Subscription Costs</h2>");
                sb.AppendLine("<table><tr><th>Subscription</th><th>Actual MTD</th><th>Forecast</th><th>Currency</th></tr>");
                foreach (var kv in data.Costs)
                {
                    sb.AppendLine($"<tr><td>{kv.Key}</td><td>{kv.Value.Actual:N2}</td>" +
                                  $"<td>{kv.Value.Forecast:N2}</td><td>{kv.Value.Currency}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // AHB section
            if (data.Ahb != null && data.Ahb.TotalOpportunities > 0)
            {
                sb.AppendLine($"<h2>Azure Hybrid Benefit Opportunities ({data.Ahb.TotalOpportunities})</h2>");
                AppendItemsTable(sb, data.Ahb.WindowsVMs, "Windows VMs",
                    new[] { "Name", "ResourceGroup", "SubscriptionId", "Location", "VmSize", "CurrentLicense" });
                AppendItemsTable(sb, data.Ahb.SqlVMs, "SQL VMs",
                    new[] { "Name", "ResourceGroup", "SubscriptionId", "CurrentLicense", "SqlEdition" });
                AppendItemsTable(sb, data.Ahb.SqlDatabases, "SQL Databases",
                    new[] { "Name", "ResourceGroup", "SubscriptionId", "CurrentLicense", "Sku" });
            }

            // Optimization section
            if (data.Optimization?.Recommendations?.Count > 0)
            {
                sb.AppendLine($"<h2>Optimization Recommendations ({data.Optimization.TotalCount})</h2>");
                sb.AppendLine("<table><tr><th>Subscription</th><th>Category</th><th>Impact</th><th>Problem</th><th>Annual Savings</th></tr>");
                foreach (var r in data.Optimization.Recommendations)
                {
                    sb.AppendLine($"<tr><td>{r.Subscription}</td><td>{r.Category}</td><td>{r.Impact}</td>" +
                                  $"<td>{HtmlEncode(r.Problem)}</td><td>{r.AnnualSavings?.ToString("N2") ?? "-"}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // Orphaned resources
            if (data.OrphanedResources?.Orphans?.Count > 0)
            {
                sb.AppendLine($"<h2>Orphaned Resources ({data.OrphanedResources.TotalCount})</h2>");
                sb.AppendLine("<table><tr><th>Category</th><th>Name</th><th>Resource Group</th><th>Location</th><th>Detail</th><th>Impact</th></tr>");
                foreach (var o in data.OrphanedResources.Orphans)
                {
                    sb.AppendLine($"<tr><td>{o.Category}</td><td>{o.ResourceName}</td><td>{o.ResourceGroup}</td>" +
                                  $"<td>{o.Location}</td><td>{HtmlEncode(o.Detail)}</td><td>{o.Impact}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // Warnings
            if (data.Warnings?.Count > 0)
            {
                sb.AppendLine("<h2>Scan Warnings</h2><ul>");
                foreach (var w in data.Warnings)
                    sb.AppendLine($"<li>{HtmlEncode(w)}</li>");
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("</body></html>");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static string HtmlHeader(string title) => $@"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8"">
<title>{HtmlEncode(title)}</title>
<style>
body{{font-family:Segoe UI,Arial,sans-serif;margin:20px;color:#333}}
h1{{color:#0078d4}}h2{{color:#005a9e;margin-top:24px}}
table{{border-collapse:collapse;width:100%;margin-bottom:16px}}
th{{background:#0078d4;color:#fff;padding:6px 10px;text-align:left}}
td{{border:1px solid #ddd;padding:5px 10px}}
tr:nth-child(even){{background:#f5f5f5}}
</style></head>";

        private static string HtmlTr(string label, string value)
            => $"<tr><th>{HtmlEncode(label)}</th><td>{HtmlEncode(value)}</td></tr>";

        private static string HtmlEncode(string? s)
            => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        private static void AppendItemsTable<T>(StringBuilder sb, List<T>? items, string title, string[] columns)
        {
            if (items == null || items.Count == 0) return;
            sb.AppendLine($"<h3>{HtmlEncode(title)} ({items.Count})</h3>");
            sb.Append("<table><tr>");
            foreach (var c in columns) sb.Append($"<th>{HtmlEncode(c)}</th>");
            sb.AppendLine("</tr>");
            foreach (var item in items)
            {
                sb.Append("<tr>");
                var type = typeof(T);
                foreach (var c in columns)
                {
                    var prop = type.GetProperty(c);
                    var val  = prop?.GetValue(item)?.ToString() ?? string.Empty;
                    sb.Append($"<td>{HtmlEncode(val)}</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }

        // ── CSV row builders ──────────────────────────────────────────────────

        private static IEnumerable<Dictionary<string, string>> GetCostRows(ScanData data)
        {
            foreach (var kv in data.Costs ?? new Dictionary<string, SubscriptionCost>())
            {
                yield return new Dictionary<string, string>
                {
                    ["SubscriptionId"] = kv.Key,
                    ["ActualMTD"]      = kv.Value.Actual.ToString(CultureInfo.InvariantCulture),
                    ["Forecast"]       = kv.Value.Forecast.ToString(CultureInfo.InvariantCulture),
                    ["Currency"]       = kv.Value.Currency
                };
            }
        }

        private static IEnumerable<Dictionary<string, string>> GetResourceCostRows(ScanData data)
        {
            foreach (var r in data.ResourceCosts ?? new List<ResourceCostItem>())
            {
                yield return new Dictionary<string, string>
                {
                    ["ResourcePath"]  = r.ResourcePath,
                    ["ResourceGroup"] = r.ResourceGroup,
                    ["ResourceType"]  = r.ResourceType,
                    ["Actual"]        = r.Actual.ToString(CultureInfo.InvariantCulture),
                    ["Currency"]      = r.Currency,
                    ["SubscriptionId"] = r.SubscriptionId
                };
            }
        }

        private static IEnumerable<Dictionary<string, string>> GetTagRows(ScanData data)
        {
            foreach (var kv in data.Tags?.TagNames ?? new Dictionary<string, TagEntry>())
            {
                foreach (var v in kv.Value.Values)
                {
                    yield return new Dictionary<string, string>
                    {
                        ["TagName"]       = kv.Key,
                        ["TagValue"]      = v.Value,
                        ["ResourceCount"] = v.ResourceCount.ToString(),
                        ["ResourceTypes"] = string.Join(", ", v.ResourceTypes)
                    };
                }
            }
        }

        private static IEnumerable<Dictionary<string, string>> GetAhbRows(ScanData data)
        {
            foreach (var vm in data.Ahb?.WindowsVMs ?? new List<AhbItem>())
                yield return AhbItemRow(vm, "Windows VM");
            foreach (var vm in data.Ahb?.SqlVMs ?? new List<AhbItem>())
                yield return AhbItemRow(vm, "SQL VM");
            foreach (var db in data.Ahb?.SqlDatabases ?? new List<AhbItem>())
                yield return AhbItemRow(db, "SQL Database");
        }

        private static Dictionary<string, string> AhbItemRow(AhbItem item, string resourceType)
        {
            return new Dictionary<string, string>
            {
                ["ResourceType"]   = resourceType,
                ["Name"]           = item.Name,
                ["ResourceGroup"]  = item.ResourceGroup,
                ["SubscriptionId"] = item.SubscriptionId,
                ["Location"]       = item.Location,
                ["CurrentLicense"] = item.CurrentLicense
            };
        }

        private static IEnumerable<Dictionary<string, string>> GetOptRows(ScanData data)
        {
            foreach (var r in data.Optimization?.Recommendations ?? new List<OptimizationRecommendation>())
            {
                yield return new Dictionary<string, string>
                {
                    ["Subscription"]   = r.Subscription,
                    ["Category"]       = r.Category,
                    ["Impact"]         = r.Impact,
                    ["Problem"]        = r.Problem,
                    ["Solution"]       = r.Solution,
                    ["ResourceType"]   = r.ResourceType,
                    ["ResourceName"]   = r.ResourceName,
                    ["AnnualSavings"]  = r.AnnualSavings?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ["Currency"]       = r.Currency
                };
            }
        }

        private static IEnumerable<Dictionary<string, string>> GetOrphanRows(ScanData data)
        {
            foreach (var o in data.OrphanedResources?.Orphans ?? new List<OrphanedResource>())
            {
                yield return new Dictionary<string, string>
                {
                    ["Category"]       = o.Category,
                    ["ResourceName"]   = o.ResourceName,
                    ["ResourceGroup"]  = o.ResourceGroup,
                    ["SubscriptionId"] = o.SubscriptionId,
                    ["Location"]       = o.Location,
                    ["Detail"]         = o.Detail,
                    ["Impact"]         = o.Impact
                };
            }
        }

        private static IEnumerable<Dictionary<string, string>> GetBudgetRows(ScanData data)
        {
            foreach (var b in data.Budgets?.Budgets ?? new List<BudgetItem>())
            {
                yield return new Dictionary<string, string>
                {
                    ["Subscription"]   = b.Subscription,
                    ["BudgetName"]     = b.BudgetName,
                    ["Amount"]         = b.Amount.ToString(CultureInfo.InvariantCulture),
                    ["TimeGrain"]      = b.TimeGrain,
                    ["ActualSpend"]    = b.ActualSpend.ToString(CultureInfo.InvariantCulture),
                    ["Forecast"]       = b.Forecast.ToString(CultureInfo.InvariantCulture),
                    ["PctUsed"]        = b.PctUsed.ToString(CultureInfo.InvariantCulture),
                    ["Risk"]           = b.Risk,
                    ["Currency"]       = b.Currency
                };
            }
        }
    }
}
