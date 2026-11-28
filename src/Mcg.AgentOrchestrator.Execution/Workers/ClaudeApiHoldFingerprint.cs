using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ClaudeApiHoldFingerprint
{
    internal const string SecretFileName = "provider-hold-fingerprint.key";
    private const string Prefix = "api-";
    private const int SecretLength = 32;
    private const int ShortHexLength = 16;
    private const int LegacyHexLength = 64;
    private const int MaxReadAttempts = 3;
    private const int ReadRetryDelayMilliseconds = 20;

    private static readonly ClaudeApiHoldFingerprint Machine =
        new(() => LoadSecret(MachineStateDirectory(), PublishSecret, File.OpenRead));
    private readonly Lazy<byte[]> _secret;

    private ClaudeApiHoldFingerprint(Func<byte[]> loadSecret) => _secret = new(loadSecret);

    internal static ClaudeApiHoldFingerprint ForMachine() => Machine;

    internal static ClaudeApiHoldFingerprint FromStateDirectory(string directory) =>
        FromStateDirectory(directory, PublishSecret);

    internal static ClaudeApiHoldFingerprint FromStateDirectory(
        string directory, Action<string, string> publish, Func<string, Stream>? openRead = null) =>
        new(() => LoadSecret(directory, publish, openRead ?? File.OpenRead));

    internal static ClaudeApiHoldFingerprint FromSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretLength)
            throw new ArgumentException("An API hold fingerprint secret must be 32 bytes.", nameof(secret));

        var ownedSecret = secret.ToArray();
        return new(() => ownedSecret);
    }

    internal string Compute(string apiKey)
    {
        var hash = HMACSHA256.HashData(_secret.Value, Encoding.UTF8.GetBytes(apiKey));
        return Prefix + Convert.ToHexStringLower(hash.AsSpan(0, ShortHexLength / 2));
    }

    internal bool IsCurrentKeyIdentity(string? stored, string? apiKey, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || stored is null || !stored.StartsWith(Prefix, comparison))
            return false;

        var hex = stored.AsSpan(Prefix.Length);
        if (hex.Length is not (ShortHexLength or LegacyHexLength) ||
            hex.ContainsAnyExcept("0123456789abcdefABCDEF"))
            return false;

        var current = hex.Length == ShortHexLength
            ? Compute(apiKey)
            : Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        return string.Equals(stored, current, comparison);
    }

    public override string ToString() => nameof(ClaudeApiHoldFingerprint);

    private static string MachineStateDirectory()
    {
        // OrchestratorDataRoot exports the resolved directory before App consumers run.
        var configured = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DATA")?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return Path.GetFullPath(configured);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcg-agent-orchestrator")
            : Path.Combine(localAppData, "Mcg.AgentOrchestrator");
    }

    private static byte[] LoadSecret(string directory, Action<string, string> publish, Func<string, Stream> openRead)
    {
        var path = Path.Combine(directory, SecretFileName);
        try
        {
            try { return ReadSecret(path, openRead); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            var output = new FileStream(temporary, options);
            try
            {
                using (output)
                {
                    output.Write(RandomNumberGenerator.GetBytes(SecretLength));
                    output.Flush(flushToDisk: true);
                }

                try { publish(temporary, path); }
                catch (Exception publishError)
                {
                    // Another initializer may have published the key before the move failed.
                    try { return ReadSecret(path, openRead); }
                    catch { throw CreateUnavailableError(path, publishError); }
                }

                return ReadSecret(path, openRead);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw CreateUnavailableError(path, error);
        }
    }

    private static void PublishSecret(string temporary, string path) => File.Move(temporary, path, overwrite: false);

    private static InvalidOperationException CreateUnavailableError(string path, Exception cause) =>
        new($"API hold fingerprint secret is unavailable at '{path}'.", cause);

    private static byte[] ReadSecret(string path, Func<string, Stream> openRead)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var input = openRead(path);
                if (input.Length != SecretLength)
                    throw new InvalidOperationException($"API hold fingerprint secret has an invalid length at '{path}'.");

                var secret = new byte[SecretLength];
                input.ReadExactly(secret);
                return secret;
            }
            catch (Exception error) when (attempt < MaxReadAttempts &&
                error is (IOException and not (FileNotFoundException or DirectoryNotFoundException)) or UnauthorizedAccessException)
            {
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }
    }
}
