namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerProviderSignIn
{
    internal static bool RequiresSignIn(string diagnosis) =>
        diagnosis.Contains("last confirmed: authentication-required", StringComparison.Ordinal);

    internal static string? Provider(string? bindingKey, string? diagnosis)
    {
        if (diagnosis is not { Length: > 0 } || !RequiresSignIn(diagnosis) || bindingKey is null) return null;
        var separator = bindingKey.IndexOf("::", StringComparison.Ordinal);
        return separator > 0 && bindingKey[..separator].Trim() is { Length: > 0 } provider ? provider : null;
    }
}
