using System.Globalization;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns inline framing: payload characters are never trimmed or normalized.
internal static class WorkerContextInlinePayload
{
    private const string InlinePrefix = "INLINE FULL: identity=";
    private const string RawEncoding = "utf8-raw";
    private const string CharsPrefix = RawEncoding + "; chars=";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Render(string headerPrefix, string text, string hash) =>
        headerPrefix + CharsPrefix + text.Length.ToString(CultureInfo.InvariantCulture) +
        Environment.NewLine + text + Environment.NewLine + "END INLINE " + hash;

    internal static byte[] Rehydrate(
        string renderedPackage,
        string headerPrefix,
        LogicalArtifactIdentity identity,
        string hash)
    {
        var matches = 0;
        var payloadStart = -1;
        var markerStart = -1;
        var encoding = string.Empty;
        var framing = string.Empty;
        for (var offset = 0; offset < renderedPackage.Length;)
        {
            var line = ReadLine(renderedPackage, offset, out var next, out var separator);
            var isMatch = line.StartsWith(headerPrefix, StringComparison.Ordinal);
            var encodingStart = line.LastIndexOf("; encoding=", StringComparison.Ordinal);
            if (line.StartsWith(InlinePrefix, StringComparison.Ordinal) && encodingStart >= 0)
            {
                var metadata = line[(encodingStart + "; encoding=".Length)..];
                var rawMarkerStart = -1;
                if (IsRaw(metadata))
                {
                    var hashStart = line.LastIndexOf("; sha256=", StringComparison.Ordinal);
                    var hashEnd = hashStart < 0 ? -1 : line.IndexOf(';', hashStart + "; sha256=".Length);
                    if (hashEnd < 0)
                        throw Mismatch(identity);
                    var rawHash = line[(hashStart + "; sha256=".Length)..hashEnd];
                    (rawMarkerStart, next) = FindMarker(renderedPackage, next, rawHash, identity);
                }

                if (isMatch)
                {
                    matches++;
                    payloadStart = offset + line.Length + separator.Length;
                    markerStart = rawMarkerStart;
                    encoding = metadata;
                    framing = separator;
                }
            }

            // A raw payload may itself quote headers. Resume after its end marker.
            offset = next;
        }

        if (matches != 1 || (framing.Length == 0 && !IsRaw(encoding)))
        {
            throw new WorkerContextPreparationException(
                identity,
                "inline-rehydration-ambiguous",
                $"Expected exactly one rendered inline payload, found {matches}.");
        }

        return IsRaw(encoding)
            ? RecoverRaw(renderedPackage, payloadStart, markerStart, framing, encoding, identity, hash)
            : WorkerContextPackageBuilder.RecoverInlineBytes(
                ReadLine(renderedPackage, payloadStart, out _, out _), encoding);
    }

    private static bool IsRaw(string encoding) =>
        encoding.Equals(RawEncoding, StringComparison.Ordinal) ||
        encoding.StartsWith(RawEncoding + ";", StringComparison.Ordinal);

    private static byte[] RecoverRaw(
        string package,
        int payloadStart,
        int markerStart,
        string framing,
        string metadata,
        LogicalArtifactIdentity identity,
        string hash)
    {
        if (!metadata.StartsWith(CharsPrefix, StringComparison.Ordinal) ||
            !int.TryParse(metadata.AsSpan(CharsPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var chars) ||
            framing.Length == 0 || markerStart - framing.Length < payloadStart ||
            !package.AsSpan(markerStart - framing.Length, framing.Length).SequenceEqual(framing.AsSpan()))
        {
            throw Mismatch(identity);
        }

        var text = package.AsSpan(payloadStart, markerStart - framing.Length - payloadStart);
        if (text.Length != chars)
            throw Mismatch(identity);

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text.ToString());
        }
        catch (EncoderFallbackException)
        {
            throw Mismatch(identity);
        }

        if (!WorkerContextArtifact.Hash(bytes).Equals(hash, StringComparison.Ordinal))
            throw Mismatch(identity);
        return bytes;
    }

    private static (int Start, int Next) FindMarker(
        string package, int payloadStart, string hash, LogicalArtifactIdentity identity)
    {
        var marker = "END INLINE " + hash;
        for (var offset = payloadStart; offset < package.Length;)
        {
            var line = ReadLine(package, offset, out var next, out _);
            if (line.Equals(marker, StringComparison.Ordinal))
                return (offset, next);
            offset = next;
        }

        throw Mismatch(identity);
    }

    private static string ReadLine(string text, int start, out int next, out string separator)
    {
        var end = text.IndexOf('\n', start);
        if (end < 0)
        {
            next = text.Length;
            separator = string.Empty;
            return text[start..];
        }

        next = end + 1;
        separator = end > start && text[end - 1] == '\r' ? "\r\n" : "\n";
        return text[start..(next - separator.Length)];
    }

    private static WorkerContextPreparationException Mismatch(LogicalArtifactIdentity identity) =>
        new(identity, "inline-raw-mismatch", "Raw inline framing, character count, or content hash did not match the header.");
}
