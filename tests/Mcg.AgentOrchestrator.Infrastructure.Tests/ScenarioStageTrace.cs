using System.Diagnostics;
using System.Text;

// A trace has at most one active stage; completion means execution ended, including by exception.
internal sealed class ScenarioStageTrace : IDisposable
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private string? _activeStage;

    public ScenarioStageTrace(string directory, string scenarioName)
    {
        Directory.CreateDirectory(directory);
        TracePath = Path.Combine(directory, $"{scenarioName}-{Guid.NewGuid():N}.trace.log");
        _stream = new FileStream(TracePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string RetainedRoot
    {
        get
        {
            // AssemblyTempRedirect.Install deletes the process temp root on exit
            // (AssemblyTempRedirect.cs:78). Retained scenario evidence must live outside it.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var temp = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(local)
                ? Path.Combine(local, "Temp")
                : Path.GetTempPath();
            return Path.Combine(temp, "mcg-scenario-stage-traces");
        }
    }

    public string TracePath { get; }

    public string? ActiveStage
    {
        get { lock (_sync) return _activeStage; }
    }

    public void Start(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (stage.Contains('\r') || stage.Contains('\n'))
            throw new ArgumentException("A stage name must fit on one line.", nameof(stage));
        lock (_sync)
        {
            if (_activeStage is not null)
                throw new InvalidOperationException($"Stage '{_activeStage}' is still active.");
            Write("started", stage);
            _activeStage = stage;
        }
    }

    public void Complete(string stage)
    {
        lock (_sync)
        {
            if (_activeStage is null || !string.Equals(_activeStage, stage, StringComparison.Ordinal))
                throw new InvalidOperationException($"Stage '{stage}' is not the active stage.");
            Write("completed", stage);
            _activeStage = null;
        }
    }

    public void Run(string stage, Action body) => Run(stage, () =>
    {
        body();
        return 0;
    });

    public T Run<T>(string stage, Func<T> body)
    {
        Start(stage);
        try
        {
            return body();
        }
        catch (Xunit.Sdk.XunitException exception)
        {
            // Capture before finally completes the stage and clears ActiveStage.
            throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}{Describe()}", exception);
        }
        finally
        {
            Complete(stage);
        }
    }

    public string Describe()
    {
        lock (_sync) return $"Trace: {TracePath}; active stage: {_activeStage ?? "<none>"}";
    }

    private void Write(string marker, string stage)
    {
        _writer.WriteLine(FormattableString.Invariant($"{_clock.ElapsedMilliseconds} {marker} {stage}"));
        _writer.Flush();
        _stream.Flush(flushToDisk: false);
    }

    public void Dispose()
    {
        lock (_sync) _writer.Dispose();
    }
}
