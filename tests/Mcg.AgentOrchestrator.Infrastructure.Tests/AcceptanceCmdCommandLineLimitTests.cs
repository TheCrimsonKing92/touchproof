using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: validates composed strings without processes, I/O, or shared state.
public sealed class AcceptanceCmdCommandLineLimitTests
{
    [Fact]
    public void ComposedCommand_At8191Characters_Passes()
    {
        var (composed, arguments) = ComposeCommand(8191);
        Assert.Equal(8191, composed.Length);

        var error = Record.Exception(() =>
            AcceptanceCmdCommandLineLimit.ThrowIfCmdCommandLineTooLong(composed, arguments));

        Assert.Null(error);
    }

    [Fact]
    public void ComposedCommand_At8192Characters_ThrowsNamingFirstArgument()
    {
        var (composed, arguments) = ComposeCommand(8192);
        Assert.Equal(8192, composed.Length);

        var error = Assert.Throws<CommandLineTooLongException>(() =>
            AcceptanceCmdCommandLineLimit.ThrowIfCmdCommandLineTooLong(composed, arguments));

        Assert.Equal(8192, error.Length);
        Assert.Equal(8191, error.Limit);
        Assert.Equal(arguments[0], error.FirstArgument);
        Assert.Contains($"first argument: {arguments[0]}", error.Message);
    }

    private static (string Composed, string[] Arguments) ComposeCommand(int length)
    {
        const string firstArgument = @"C:\tools\acceptance runner.cmd";
        // Match cmd /c's outer quotes and the quotes around each argument.
        var prefix = $"/c \"\"{firstArgument}\" \"";
        const string suffix = "\"\"";
        string[] arguments = [firstArgument, new string('x', length - prefix.Length - suffix.Length)];
        return (prefix + arguments[1] + suffix, arguments);
    }
}
