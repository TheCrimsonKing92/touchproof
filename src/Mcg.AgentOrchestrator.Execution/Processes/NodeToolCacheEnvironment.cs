namespace Mcg.AgentOrchestrator.Infrastructure;

public static class NodeToolCacheEnvironment
{
    public static void Apply(IDictionary<string, string?> environment, string sandboxRoot)
    {
        var root = Path.Combine(Path.GetFullPath(sandboxRoot), "node");
        environment["npm_config_cache"] = Path.Combine(root, "npm-cache");
        environment["npm_config_store_dir"] = Path.Combine(root, "pnpm-store");
        environment["COREPACK_HOME"] = Path.Combine(root, "corepack");
        environment["YARN_CACHE_FOLDER"] = Path.Combine(root, "yarn-cache");
        environment["YARN_GLOBAL_FOLDER"] = Path.Combine(root, "yarn-global");
        environment["BUN_INSTALL_CACHE_DIR"] = Path.Combine(root, "bun-cache");
        environment["TEMP"] = environment["TMP"] = environment["TMPDIR"] = Path.Combine(root, "temp");
    }
}
