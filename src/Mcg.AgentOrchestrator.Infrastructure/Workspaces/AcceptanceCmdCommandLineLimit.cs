using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceCmdCommandLineLimit
{
    internal const int MaxCmdCommandLineLength = 8191;
    internal const int MaxDirectCommandLineLength = 32767;

    internal static int ThrowIfDirectCommandLineTooLong(ProcessStartInfo startInfo)
    {
        // Match the owned launcher's MSVC quoting, and include CreateProcessW's terminating null.
        var length = OwnedProcessGroup.QuoteCommandArgument(startInfo.FileName).Length + 1;
        foreach (var argument in startInfo.ArgumentList)
            length += 1 + OwnedProcessGroup.QuoteCommandArgument(argument).Length;
        if (length > MaxDirectCommandLineLength)
            throw new CommandLineTooLongException(length, MaxDirectCommandLineLength,
                startInfo.FileName, launcher: "CreateProcessW");
        return length - 1;
    }

    internal static void ThrowIfCmdCommandLineTooLong(string composedArguments, string[] arguments)
    {
        if (composedArguments.Length > MaxCmdCommandLineLength)
            throw new CommandLineTooLongException(composedArguments.Length, MaxCmdCommandLineLength,
                arguments.Length == 0 ? "(none)" : arguments[0]);
    }
}
