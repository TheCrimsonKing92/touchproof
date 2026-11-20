using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// A frozen file inventory with retained text and line views. Collected trees are reparsed from that text.
internal sealed class RepositorySourceSnapshot
{
    private readonly IReadOnlyDictionary<string, SourceFile> filesByPath;
    internal ISourceSnapshotObserver Observer { get; }
    public string Root { get; }
    public IReadOnlyList<string> Files { get; }

    internal RepositorySourceSnapshot(string root, ISourceSnapshotObserver observer)
    {
        Root = root;
        Observer = observer;
        var paths = new[] { "tests", "src" }
            .Select(area => Path.Combine(root, area))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Files = Array.AsReadOnly(paths);
        filesByPath = paths.ToDictionary(path => path, path => new SourceFile(root, path, observer),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        observer.OnEnumerated(root);
    }

    public IEnumerable<string> EnumerateFiles(string directory)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(Root, directory));
        var relative = Path.GetRelativePath(Root, full).Replace('\\', '/').TrimEnd('/');
        if (relative != "tests" && relative != "src" &&
            !relative.StartsWith("tests/", StringComparison.Ordinal) && !relative.StartsWith("src/", StringComparison.Ordinal))
            throw new ArgumentException("The directory must be inside the repository tests or src tree.", nameof(directory));
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Source directory does not exist: {full}");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Files.Where(path => path.StartsWith(relative + "/", comparison)).Select(FullPath);
    }

    public string GetText(string path) => FindFile(path).Text.Value;
    public IReadOnlyList<string> GetLines(string path) => FindFile(path).Lines.Value;
    public SyntaxTree GetSyntaxTree(string path) => FindFile(path).GetTree();

    private string FullPath(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    private SourceFile FindFile(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : FullPath(path));
        var relative = Path.GetRelativePath(Root, full).Replace('\\', '/');
        if (!filesByPath.TryGetValue(relative, out var file))
            throw new FileNotFoundException("The file is not in the repository source snapshot.", full);
        return file;
    }

    private sealed class SourceFile
    {
        private readonly string root;
        private readonly string relativePath;
        private readonly string fullPath;
        private readonly ISourceSnapshotObserver observer;
        private readonly object treeGate = new();
        private WeakReference<SyntaxTree>? treeReference;

        public readonly Lazy<string> Text;
        public readonly Lazy<IReadOnlyList<string>> Lines;

        public SourceFile(string root, string relativePath, ISourceSnapshotObserver observer)
        {
            this.root = root;
            this.relativePath = relativePath;
            this.observer = observer;
            fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Text = new Lazy<string>(() =>
            {
                var text = File.ReadAllText(fullPath);
                this.observer.OnTextRead(this.root, this.relativePath);
                return text;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            Lines = new Lazy<IReadOnlyList<string>>(() => new SourceSnapshotLineView(Text.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public SyntaxTree GetTree()
        {
            lock (treeGate)
            {
                if (treeReference is not null && treeReference.TryGetTarget(out var liveTree))
                    return liveTree;

                var tree = CSharpSyntaxTree.ParseText(Text.Value, path: fullPath);
                observer.OnParsed(root, relativePath);
                treeReference = new WeakReference<SyntaxTree>(tree);
                return tree;
            }
        }
    }
}
