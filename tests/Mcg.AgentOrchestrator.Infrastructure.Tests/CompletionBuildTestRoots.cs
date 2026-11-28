using System.Runtime.ExceptionServices;
using System.Text.Json;

internal static class CompletionBuildTestRoots
{
    public static string? Remove(string evidenceRoot, string receiptsPath)
    {
        var failures = new List<Exception>();
        string? profileRoot = null;
        try
        {
            profileRoot = ReadProfileRoot(receiptsPath);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (profileRoot is not null)
            RemoveRoot(profileRoot);
        RemoveRoot(evidenceRoot);

        if (failures.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1)
            throw new AggregateException(failures);

        return profileRoot;

        void RemoveRoot(string root)
        {
            try
            {
                SharedTestSupport.RemoveTempDirectory(root);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private static string? ReadProfileRoot(string receiptsPath)
    {
        try
        {
            foreach (var line in File.ReadLines(receiptsPath))
            {
                using var receipt = JsonDocument.Parse(line);
                var entry = receipt.RootElement;
                if (entry.GetProperty("kind").GetString() != "candidate")
                    continue;

                if (!entry.TryGetProperty("payload", out var payload)
                    || !payload.TryGetProperty("Profile", out var profile)
                    || profile.ValueKind != JsonValueKind.String)
                    return null;

                var path = profile.GetString();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return null;
    }
}
