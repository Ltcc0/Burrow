namespace BurrowWin.Models;

public sealed record SafeDeletionRequest(
    string Path,
    long SizeBytes,
    string ScopeRoot,
    string Source,
    DestructiveActionAuthorization Authorization,
    string? ExpectedItemType = null,
    bool BusinessRuleSatisfied = true,
    string? BusinessRuleFailure = null);
