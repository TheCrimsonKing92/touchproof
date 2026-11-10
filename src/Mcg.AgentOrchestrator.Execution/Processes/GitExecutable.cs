using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the immutable installation identity, never a snapshot of the caller's environment.
internal static class GitExecutable
{
    private static readonly Lazy<GitInstallation?> Installation = new(
        () => Recognize(HandleListProcessLauncher.ResolveApplication("git")),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool IsInstallationCreated => Installation.IsValueCreated;

    internal static bool Prepare(ProcessStartInfo info)
    {
        if (!OperatingSystem.IsWindows() || info.FileName != "git")
            return false;

        if (Installation.Value is { } installation)
        {
            info.FileName = installation.RealExecutable;
            installation.ApplyEnvironment(info.Environment);
        }
        return true;
    }

    internal static GitInstallation? Recognize(string resolvedPath)
    {
        if (!Path.IsPathFullyQualified(resolvedPath) ||
            !Path.GetFileName(resolvedPath).Equals("git.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var directory = Path.GetDirectoryName(resolvedPath)!;
        var leaf = Path.GetFileName(directory);
        if (!leaf.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
            !leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
            return null;
        var root = Path.GetDirectoryName(directory);
        if (root is null) return null;

        // The 2.53 wrapper selects clangarm64 on ARM64, otherwise mingw{32,64}.
        var systems = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? new[] { "clangarm64", "mingw64", "mingw32" }
            : new[] { "mingw64", "mingw32", "clangarm64" };
        foreach (var system in systems)
        {
            var bin = Path.Combine(root, system, "bin");
            var executable = Path.Combine(bin, "git.exe");
            if (File.Exists(executable))
                return new(root, executable, bin, system.ToUpperInvariant());
        }
        return null;
    }

    internal sealed record GitInstallation(string Root, string RealExecutable, string MsystemBin, string Msystem)
    {
        // Git for Windows v2.53.0.windows.1: setup_environment + maybe_read_config.
        // https://github.com/git-for-windows/MINGW-packages/blob/85dd204ce49231a71add2228f2e694868aec17a1/mingw-w64-git/git-wrapper.c
        internal void ApplyEnvironment(IDictionary<string, string?> environment)
        {
            string? Value(string name) => environment.TryGetValue(name, out var value) ? value : null;
            environment["MSYSTEM"] = Msystem;
            if (string.IsNullOrEmpty(Value("PLINK_PROTOCOL")))
                environment["PLINK_PROTOCOL"] = "ssh";
            if (string.IsNullOrEmpty(Value("HOME")))
            {
                var homePath = Value("HOMEPATH");
                var home = Value("HOMEDRIVE") + homePath;
                if (!string.IsNullOrEmpty(homePath) && Directory.Exists(home) &&
                    !home.Equals(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
                    environment["HOME"] = home;
                else if (!string.IsNullOrEmpty(Value("USERPROFILE")))
                    environment["HOME"] = environment["USERPROFILE"];
            }

            var prefix = MsystemBin + ";" + Path.Combine(Root, "usr", "bin") + ";";
            var effectiveHome = Value("HOME");
            // Matches the wrapper's 4096-character buffer guard (including the NUL).
            if ((effectiveHome?.Length + 1 ?? 0) + 6 < 4096)
                prefix += (string.IsNullOrEmpty(effectiveHome) ? "bin" :
                    effectiveHome.TrimEnd('\\') + "\\bin") + ";";
            environment["PATH"] = prefix + Value("PATH");

            var config = Path.Combine(Root, "etc", "git-bash.config");
            try
            {
                foreach (var line in File.ReadLines(config))
                {
                    if (!line.StartsWith("MSYS=", StringComparison.Ordinal)) continue;
                    var existing = Value("MSYS");
                    environment["MSYS"] = line[5..] + (string.IsNullOrEmpty(existing) ? "" : " " + existing);
                }
            }
            // The wrapper treats a missing/unreadable optional config as no configuration.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
