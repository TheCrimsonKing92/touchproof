using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Optional evidence about an unavailable run; never an input to attribution.
internal sealed record MergeTrainMemberRecheckUnavailable
{
    public string Code { get; }
    public string? ExceptionType { get; }
    public string? Message { get; }
    public string? Detail { get; }

    public MergeTrainMemberRecheckUnavailable(string code, string? exceptionType = null,
        string? message = null, string? detail = null)
    {
        Code = Clean(code)!;
        ExceptionType = Clean(exceptionType);
        Message = Clean(message);
        Detail = Clean(detail);
    }

    internal static MergeTrainMemberRecheckUnavailable Threw(Exception exception, string code = "runner-threw") =>
        new(code, exception.GetType().Name, exception.Message);

    internal string EventText => $" unavailable_reason={Escape(Code)}" +
        (ExceptionType is null ? "" : $" exception={Escape(ExceptionType)}") +
        (Message is null ? "" : $" message=\"{Escape(Message)}\"") +
        (Detail is null ? "" : $" detail=\"{Escape(Detail)}\"");

    private static string? Clean(string? value)
    {
        if (value is null) return null;
        var length = Math.Min(200, value.Length);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return new string(value[..length].Select(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray());
    }

    private static string Escape(string value)
    {
        var text = new StringBuilder();
        foreach (var character in value)
        {
            if (character is '\\' or '"' or ';' or '[' or ']') text.Append('\\');
            text.Append(character);
        }
        return text.ToString();
    }
}
