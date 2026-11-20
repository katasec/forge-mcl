namespace ForgeMission.Core.Tools;

public enum CapabilityPolicySource
{
    Local,
    Administrator,
}

public sealed record CapabilityAuthorizationRule(
    AuthorizationOutcome Outcome,
    CapabilityPolicySource Source = CapabilityPolicySource.Local);

// Maps capability names to authorization rules; a capability with no rule gets the default rule.
// Distributing administrator-owned rules is outside this layer; Source records where a rule came from.
public sealed class CapabilityAuthorizationPolicy
{
    private readonly IReadOnlyDictionary<string, CapabilityAuthorizationRule> _rules;

    public CapabilityAuthorizationPolicy(
        IEnumerable<KeyValuePair<string, CapabilityAuthorizationRule>> rules,
        CapabilityAuthorizationRule? defaultRule = null)
    {
        _rules = rules.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        DefaultRule = defaultRule ?? new CapabilityAuthorizationRule(AuthorizationOutcome.AutoDenied);
    }

    public IReadOnlyDictionary<string, CapabilityAuthorizationRule> Rules => _rules;

    public CapabilityAuthorizationRule DefaultRule { get; }

    public CapabilityAuthorizationRule RuleFor(string capabilityName) =>
        _rules.GetValueOrDefault(capabilityName, DefaultRule);

    public static CapabilityAuthorizationPolicy Default { get; } = new(
    [
        new KeyValuePair<string, CapabilityAuthorizationRule>(
            CapabilityNames.File, new CapabilityAuthorizationRule(AuthorizationOutcome.AutoApproved)),
        new KeyValuePair<string, CapabilityAuthorizationRule>(
            CapabilityNames.Terminal, new CapabilityAuthorizationRule(AuthorizationOutcome.AutoApproved)),
    ]);
}

public sealed class PolicyCapabilityAuthorizer(CapabilityAuthorizationPolicy policy) : ICapabilityAuthorizer
{
    public Task<AuthorizationOutcome> AuthorizeAsync(
        string capabilityName,
        object request,
        CancellationToken ct) =>
        Task.FromResult(policy.RuleFor(capabilityName).Outcome);
}
