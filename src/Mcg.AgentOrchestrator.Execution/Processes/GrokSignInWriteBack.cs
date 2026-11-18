using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GrokSignInWriteBack
{
    private const int MaximumSnapshotBytes = 1024 * 1024;

    internal static void TryWriteBack(
        Func<string> sandboxGrokHome,
        Func<string> hostGrokHome,
        Action<string>? reportDiagnostic,
        GrokSignInFileOperations? fileOperations = null)
    {
        string? stagedPath = null;
        try
        {
            var operations = fileOperations ?? GrokSignInFileOperations.Default;
            var sandboxBytes = operations.ReadSnapshot(Path.Combine(sandboxGrokHome(), "auth.json"));
            var sandboxExpiry = TryParseExpiry(sandboxBytes ?? []);
            if (sandboxExpiry is null)
                return;

            var hostDirectory = hostGrokHome();
            var hostPath = Path.Combine(hostDirectory, "auth.json");
            var hostBytes = operations.ReadSnapshot(hostPath);
            var hostExpiry = TryParseExpiry(hostBytes ?? []);
            if (hostExpiry is null || sandboxExpiry <= hostExpiry)
                return;

            stagedPath = Path.Combine(hostDirectory, $"auth.json.{Guid.NewGuid():N}.tmp");
            // Stage the same snapshot that passed comparison, inheriting the host directory's security.
            operations.WriteStaged(stagedPath, sandboxBytes!);
            operations.Replace(stagedPath, hostPath);
        }
        catch (Exception failure)
        {
            try
            {
                // Exception messages can contain credential data; report only the failure type.
                reportDiagnostic?.Invoke($"[dispatch-host] grok sign-in write-back skipped: {failure.GetType().Name}");
            }
            catch { }
        }
        finally
        {
            if (stagedPath is not null)
            {
                try { (fileOperations ?? GrokSignInFileOperations.Default).DeleteIfExists(stagedPath); }
                catch { }
            }
        }
    }

    internal static DateTimeOffset? TryParseExpiry(ReadOnlyMemory<byte> json)
    {
        if (json.IsEmpty || json.Length > MaximumSnapshotBytes)
            return null;

        // A BOM is ignored for parsing only; replacement preserves the original bytes.
        if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            json = json[3..];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var pending = new Queue<JsonElement>();
            pending.Enqueue(document.RootElement);
            while (pending.TryDequeue(out var current))
            {
                foreach (var property in current.EnumerateObject())
                {
                    var name = property.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                    if (name is "expiresat" or "expiry" or "expires" or "expiration" or "expirationtime")
                        return ParseValue(property.Value);

                    if (property.Value.ValueKind == JsonValueKind.Object)
                        pending.Enqueue(property.Value);
                }
            }
        }
        catch (JsonException) { }

        return null;
    }

    private static DateTimeOffset? ParseValue(JsonElement value)
    {
        long epoch;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out epoch))
            {
                var hasOffset = text.EndsWith('Z') ||
                    (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
                return hasOffset && value.TryGetDateTimeOffset(out var instant)
                    ? instant.ToUniversalTime()
                    : null;
            }
        }
        else if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out epoch))
        {
            return null;
        }

        if (epoch <= 0)
            return null;

        try
        {
            return epoch < 100_000_000_000
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : DateTimeOffset.FromUnixTimeMilliseconds(epoch);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal sealed class GrokSignInFileOperations
    {
        internal static GrokSignInFileOperations Default { get; } = new();

        internal Func<string, byte[]?> ReadSnapshot { get; init; } = ReadFileSnapshot;
        internal Action<string, byte[]> WriteStaged { get; init; } = (path, bytes) =>
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        };
        internal Action<string, string> Replace { get; init; } =
            (staged, host) => File.Replace(staged, host, destinationBackupFileName: null);
        internal Action<string> DeleteIfExists { get; init; } = File.Delete;

        private static byte[]? ReadFileSnapshot(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > MaximumSnapshotBytes)
                    return null;

                var bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }
}
