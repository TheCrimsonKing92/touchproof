namespace Mcg.AgentOrchestrator.Core;

public static class TrxResultClassNames
{
    public static IEnumerable<string> Aliases(string className)
    {
        yield return className;
        for (var i = 0; i < className.Length - 1; i++)
        {
            if (IsClassSeparator(className[i]))
                yield return className[(i + 1)..];
        }
    }

    public static bool IsBoundaryMatch(string identity, string token)
    {
        if (token.Length == 0) return false;

        for (var start = identity.IndexOf(token, StringComparison.Ordinal);
             start >= 0;
             start = identity.IndexOf(token, start + 1, StringComparison.Ordinal))
        {
            var end = start + token.Length;
            if ((start == 0 || IsClassSeparator(identity[start - 1])) &&
                (end == identity.Length || identity[end] == '.'))
                return true;
        }

        return false;
    }

    private static bool IsClassSeparator(char character) => character is '.' or '+';
}
