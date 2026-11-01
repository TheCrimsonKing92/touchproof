using System.Globalization;

internal static class WorkerContextInlinePayloadFixture
{
    internal static string ReadText(string prompt, string identity)
    {
        var prefix = $"INLINE FULL: identity={identity};";
        var start = prompt.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing inline delivery: {identity}");
        var headerEnd = prompt.IndexOf('\n', start);
        Assert.True(headerEnd >= 0, $"Missing inline payload: {identity}");
        var header = prompt[start..headerEnd].TrimEnd('\r');
        const string charsPrefix = "; encoding=utf8-raw; chars=";
        var charsStart = header.IndexOf(charsPrefix, StringComparison.Ordinal);
        Assert.True(charsStart >= 0, $"Missing raw character count: {identity}");
        var chars = int.Parse(header.AsSpan(charsStart + charsPrefix.Length), CultureInfo.InvariantCulture);
        var text = prompt.Substring(headerEnd + 1, chars);
        var hashStart = header.IndexOf("; sha256=", StringComparison.Ordinal) + "; sha256=".Length;
        var hash = header[hashStart..header.IndexOf(';', hashStart)];
        var framing = prompt[headerEnd - 1] == '\r' ? "\r\n" : "\n";
        Assert.StartsWith(framing + "END INLINE " + hash, prompt[(headerEnd + 1 + chars)..]);
        return text;
    }
}
