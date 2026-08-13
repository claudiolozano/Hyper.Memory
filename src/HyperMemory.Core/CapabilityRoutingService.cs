namespace HyperMemory.Core;

public sealed class CapabilityRegistry(IEnumerable<ICapabilityProvider> providers) : ICapabilityRegistry
{
    public async Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
        var discovered = new Dictionary<string, CapabilityDescriptor>(StringComparer.Ordinal);
        foreach (var provider in providers.OrderBy(item => item.ProviderId, StringComparer.Ordinal))
        {
            IReadOnlyList<CapabilityDescriptor> capabilities;
            try
            {
                capabilities = await provider.DiscoverAsync(scope, cancellationToken) ?? [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }
            foreach (var capability in capabilities)
            {
                if (string.IsNullOrWhiteSpace(capability.CapabilityId) ||
                    string.IsNullOrWhiteSpace(capability.Kind) ||
                    string.IsNullOrWhiteSpace(capability.Provider)) continue;
                var normalized = capability with
                {
                    CapabilityId = capability.CapabilityId.Trim(),
                    Kind = capability.Kind.Trim(),
                    Provider = capability.Provider.Trim(),
                    Tags = capability.Tags.Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                    Metadata = OperationalDataSanitizer.RedactMetadata(capability.Metadata)
                };
                if (discovered.TryGetValue(normalized.CapabilityId, out var existing) && !Equivalent(existing, normalized))
                    throw new InvalidOperationException(
                        $"Capability id '{normalized.CapabilityId}' was reported with conflicting definitions.");
                discovered[normalized.CapabilityId] = normalized;
            }
        }
        return discovered.Values.OrderBy(item => item.CapabilityId, StringComparer.Ordinal).ToArray();
    }

    private static bool Equivalent(CapabilityDescriptor left, CapabilityDescriptor right) =>
        string.Equals(left.CapabilityId, right.CapabilityId, StringComparison.Ordinal) &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
        left.IsAvailable == right.IsAvailable && left.RequiresAuthorization == right.RequiresAuthorization &&
        left.Tags.SequenceEqual(right.Tags, StringComparer.OrdinalIgnoreCase) &&
        DictionaryEqual(left.Metadata, right.Metadata);

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        return left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);
    }
}

public sealed class CapabilityRouter(ICapabilityRegistry registry) : ICapabilityRouter
{
    public async Task<CapabilityRoute> ResolveAsync(
        OperationalScope scope,
        IReadOnlyList<CapabilityRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(requirements);
        var capabilities = await registry.ListAsync(scope, cancellationToken);
        var selected = new Dictionary<string, CapabilityDescriptor>(StringComparer.Ordinal);
        var missing = new List<CapabilityRequirement>();
        foreach (var requirement in requirements.OrderBy(item => item.RequirementId, StringComparer.Ordinal))
        {
            ValidateRequirement(requirement);
            var matches = capabilities.Where(item => item.IsAvailable &&
                    string.Equals(item.Kind, requirement.Kind.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    requirement.RequiredTags.All(required => item.Tags.Contains(required.Trim(), StringComparer.OrdinalIgnoreCase)))
                .OrderBy(item => item.RequiresAuthorization)
                .ThenBy(item => item.CapabilityId, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0)
            {
                if (requirement.IsMandatory) missing.Add(requirement);
                continue;
            }
            selected[matches[0].CapabilityId] = matches[0];
        }
        var chosen = selected.Values.OrderBy(item => item.CapabilityId, StringComparer.Ordinal).ToArray();
        var requiresAuthorization = chosen.Any(item => item.RequiresAuthorization);
        var explanation = missing.Count > 0
            ? $"{missing.Count} mandatory capability requirement(s) are unavailable."
            : chosen.Length == 0
                ? "No capability activation is required."
                : $"{chosen.Length} available capability activation(s) selected for Hermes.";
        return new CapabilityRoute(chosen, missing, requiresAuthorization, explanation);
    }

    private static void ValidateRequirement(CapabilityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement.RequirementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement.Kind);
        if (requirement.RequiredTags.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Capability requirement tags cannot be empty.", nameof(requirement));
    }
}
