using Xunit;

namespace Mcg.AgentOrchestrator.Core.Tests;

// Parallel-safe: the helper operates on path strings only.
public sealed class TestIdentityFilenameAssumptionTests
{
    [Theory]
    [InlineData("tests/Example.Tests/ExampleTests.cs", "ExampleTests")]
    [InlineData("tests/Example.Tests/ExampleTests.CS", "ExampleTests")]
    [InlineData("tests/Example.Tests/ExampleTests.Part.cs", "ExampleTests.Part")]
    [InlineData("tests/Example.Tests/ExampleTests.json", null)]
    [InlineData("tests/Example.Tests/ExampleTests.csx", null)]
    [InlineData("tests/Example.Tests/ExampleTests.cs ", null)]
    [InlineData("tests/Example.Tests/.cs", null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void GetClassNameCandidateRequiresCSharpPathWithNonemptyStem(string? path, string? expected)
    {
        Assert.Equal(expected, TestIdentityFilenameAssumption.GetClassNameCandidate(path));
    }
}
