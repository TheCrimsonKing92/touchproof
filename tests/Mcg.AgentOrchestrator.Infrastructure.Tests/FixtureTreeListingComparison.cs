using System.Text;

internal static class FixtureTreeListingComparison
{
    internal sealed record Result(
        IReadOnlyList<string> OnlyInFreshFixture,
        IReadOnlyList<string> OnlyInTemplateCopy,
        IReadOnlyList<string> ChangedObjectId);

    internal static Result Compare(string freshFixtureListing, string templateCopyListing)
    {
        var fresh = Parse(freshFixtureListing);
        var copy = Parse(templateCopyListing);
        return new Result(
            fresh.Keys.Except(copy.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            copy.Keys.Except(fresh.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            fresh.Keys.Where(path => copy.TryGetValue(path, out var objectId)
                    && !string.Equals(fresh[path], objectId, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray());
    }

    internal static string FailureMessage(
        string repositoryLabel, string freshFixtureTree, string templateCopyTree, Result result)
    {
        var message = new StringBuilder()
            .AppendLine($"Fixture trees differ for repository: {repositoryLabel}")
            .AppendLine($"Fresh fixture tree: {freshFixtureTree.TrimEnd()}")
            .AppendLine($"Template copy tree: {templateCopyTree.TrimEnd()}");
        AppendPaths(message, "Only in fresh fixture", result.OnlyInFreshFixture);
        AppendPaths(message, "Only in template copy", result.OnlyInTemplateCopy);
        AppendPaths(message, "Changed object id", result.ChangedObjectId);
        return message.ToString();
    }

    private static Dictionary<string, string> Parse(string listing)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in listing.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var tab = line.IndexOf('\t');
            if (tab < 0)
                throw new FormatException($"Invalid git ls-tree entry: {line}");
            var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length < 3)
                throw new FormatException($"Invalid git ls-tree entry: {line}");
            entries.Add(line[(tab + 1)..], metadata[2]);
        }
        return entries;
    }

    private static void AppendPaths(StringBuilder message, string label, IReadOnlyList<string> paths)
    {
        message.AppendLine($"{label}:");
        if (paths.Count == 0)
            message.AppendLine("  (none)");
        else
            foreach (var path in paths)
                message.AppendLine($"  {path}");
    }
}
