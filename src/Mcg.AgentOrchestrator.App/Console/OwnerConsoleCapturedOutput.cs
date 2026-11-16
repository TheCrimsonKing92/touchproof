using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleCapturedOutput : IOwnerConsoleOutput
{
    private readonly StringBuilder _text = new();
    public void Write(string text) => _text.Append(text);
    public void WriteLine(string text) => _text.AppendLine(text);
    internal string Text => _text.ToString();
    internal OwnerConsoleStructuredText? Structured { get; set; }
    internal bool Acknowledged { get; private set; }
    internal void MarkAcknowledged() => Acknowledged = true;

    internal bool TryAcknowledgementLine(out string line)
    {
        line = Text;
        if (line.EndsWith("\r\n", StringComparison.Ordinal)) line = line[..^2];
        else if (line.EndsWith('\r') || line.EndsWith('\n')) line = line[..^1];
        return line.Length > 0 && line.IndexOfAny(['\r', '\n']) < 0;
    }
}
