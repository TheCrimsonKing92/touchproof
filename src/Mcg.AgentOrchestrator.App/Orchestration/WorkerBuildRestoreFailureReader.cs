using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reads request-local evidence only; no cache, project or environment lookups.
internal static class WorkerBuildRestoreFailureReader
{
    internal sealed record RestoreFailure(string ErrorCode, string? PackageId, string? PackageVersion);

    private const int CharacterLimit = 4 * 1024 * 1024;
    private const string VersionPattern = @"\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?";
    private static readonly Regex Severity = Pattern(@"\b(?<severity>error|warning)\s*(?:[A-Z]+\d+\s*)?:");
    private static readonly Regex NuError = Pattern(@"\berror\s+(?<code>NU\d{4})\s*:");
    private static readonly Regex Denial = Pattern("""
        \baccess\s+to\s+the\s+path\s+['"](?<path>[^'"\r\n]+)['"]\s+is\s+denied\b|\b(?:access\s+(?:is\s+)?denied|permission\s+denied|EACCES|EPERM)\s*[:,]?\s*['"](?<path>[^'"\r\n]+)['"]
        """);
    private static readonly Regex CacheEntry = Pattern(
        @"[\\/]packages[\\/](?<id>[A-Za-z0-9_.-]+)[\\/](?<version>" + VersionPattern + @")(?=[\\/\s'""\])]|$)");
    private static readonly Regex PackageWithVersion = Pattern(
        """\bpackage\s+['"]?(?<id>[A-Za-z0-9_][A-Za-z0-9_.-]*)['"]?\s+(?:with\s+version\s*(?:\(\s*[<>=,\s]*)?)?(?<version>""" +
        VersionPattern + """)(?=[\s'"\)\],:]|$)""");
    private static readonly Regex MissingPackage = Pattern(
        """\bunable\s+to\s+find\s+package\s+['"]?(?<id>[A-Za-z0-9_][A-Za-z0-9_.-]*)""");

    internal static RestoreFailure? Read(string stdoutPath, string stderrPath) =>
        ReadFile(stderrPath) ?? ReadFile(stdoutPath);

    private static RestoreFailure? ReadFile(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            var line = new StringBuilder();
            // Bound even a malformed log with no line endings.
            for (var count = 0; count < CharacterLimit; count++)
            {
                var next = reader.Read();
                if (next < 0) return Recognize(line.ToString());
                if (next != '\n') { line.Append((char)next); continue; }
                if (Recognize(line.ToString()) is { } failure) return failure;
                line.Clear();
            }
            // Do not classify a truncated diagnostic.
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Each log is independent; missing, locked or unreadable evidence is absent.
            return null;
        }
    }

    private static RestoreFailure? Recognize(string line)
    {
        var severity = Severity.Match(line);
        if (!severity.Success || !severity.Groups["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase))
            return null;
        var nu = NuError.Match(line);
        var denial = Denial.Match(line, severity.Index + severity.Length);
        // A cache path elsewhere on a copy/build error is not the denied target.
        if (denial.Success && CacheEntry.Match(denial.Groups["path"].Value) is { Success: true } cache)
            return new(nu.Success ? nu.Groups["code"].Value.ToUpperInvariant() : "ACCESS_DENIED",
                cache.Groups["id"].Value, cache.Groups["version"].Value);
        if (!nu.Success) return null;
        var message = line[(nu.Index + nu.Length)..];
        // Free prose such as "package downgrade" or "package version management"
        // does not identify a package. Require an adjacent version or a known message shape.
        var package = PackageWithVersion.Match(message);
        if (!package.Success) package = MissingPackage.Match(message);
        return new(nu.Groups["code"].Value.ToUpperInvariant(),
            package.Success ? package.Groups["id"].Value.TrimEnd('.') : null,
            package.Groups["version"].Success ? package.Groups["version"].Value : null);
    }

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
