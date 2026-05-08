using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FinOpsMultitool.Models;

namespace FinOpsMultitool.Services
{
    /// <summary>
    /// Apply and remove tags via ARM Tags API (PATCH merge).
    /// Equivalent to Deploy-ResourceTag.ps1.
    /// </summary>
    public class TagDeployService
    {
        private readonly AzureRestService _rest;
        public Action<string>? StatusCallback { get; set; }

        public TagDeployService(AzureRestService rest) => _rest = rest;

        public async Task<(bool Success, string Message)> DeployTagAsync(
            string tagName, string tagValue, string scope, CancellationToken ct = default)
        {
            if (!scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                return (false, "Invalid scope: must start with /subscriptions/{guid}.");

            if (tagName.IndexOfAny(new[] { '<', '>', '&', '\'', '"', '\\' }) >= 0)
                return (false, "Tag name contains invalid characters.");

            StatusCallback?.Invoke($"Deploying tag '{tagName}={tagValue}' to {scope}...");

            var body = JsonSerializer.Serialize(new
            {
                operation  = "Merge",
                properties = new { tags = new Dictionary<string, string> { [tagName] = tagValue } }
            });

            var path = $"{scope}/providers/Microsoft.Resources/tags/default?api-version=2021-04-01";
            var doc = await _rest.PatchAsync(path, body, ct);

            if (doc != null)
                return (true, $"Tag '{tagName}={tagValue}' applied to {scope}");

            return (false, "Tag deployment failed – check permissions and scope.");
        }

        public async Task<(bool Success, string Message)> RemoveTagAsync(
            string tagName, string scope, CancellationToken ct = default)
        {
            if (!scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                return (false, "Invalid scope.");

            StatusCallback?.Invoke($"Removing tag '{tagName}' from {scope}...");

            // Get existing tags first
            var getPath = $"{scope}/providers/Microsoft.Resources/tags/default?api-version=2021-04-01";
            var current = await _rest.GetAsync(getPath, ct);

            var existingTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (current?.RootElement.TryGetProperty("properties", out var props) == true &&
                props.TryGetProperty("tags", out var tagsEl))
            {
                foreach (var kv in tagsEl.EnumerateObject())
                    existingTags[kv.Name] = kv.Value.GetString() ?? string.Empty;
            }

            existingTags.Remove(tagName);

            var body = JsonSerializer.Serialize(new
            {
                operation  = "Replace",
                properties = new { tags = existingTags }
            });

            var putPath = $"{scope}/providers/Microsoft.Resources/tags/default?api-version=2021-04-01";
            var doc = await _rest.PutAsync(putPath, body, ct);

            return doc != null
                ? (true, $"Tag '{tagName}' removed from {scope}")
                : (false, "Tag removal failed.");
        }

        /// <summary>
        /// Returns scopes: subscriptions and their resource groups.
        /// </summary>
        public async Task<List<(string DisplayName, string ResourceId)>> GetScopesAsync(
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
                            string rgName = rg.TryGetProperty("name", out var nm)
                                ? nm.GetString() ?? string.Empty : string.Empty;
                            string rgId = rg.TryGetProperty("id", out var id)
                                ? id.GetString() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrEmpty(rgName))
                                scopes.Add(($"  RG: {sub.Name} / {rgName}", rgId));
                        }
                    }
                }
                catch { }
            }

            return scopes;
        }
    }
}
