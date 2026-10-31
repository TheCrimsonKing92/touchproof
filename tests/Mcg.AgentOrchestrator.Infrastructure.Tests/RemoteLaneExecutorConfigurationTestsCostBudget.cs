using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneExecutorConfigurationTestsCostBudget
{
    [Theory]
    [InlineData("", null)]
    [InlineData(",\"costBudget\":3.5", 3.5)]
    public void OptionalBudgetIsAbsentOrLoadsForBothTransports(string property, double? expected)
    {
        foreach (var transport in new[] { "", ",\"transport\":\"ssh\",\"runnerAlias\":\"runner\",\"adminAlias\":\"admin\",\"remoteRepository\":\"C:/repo\"" })
            WithFile(property + transport, configuration =>
            {
                Assert.True(configuration.Enabled);
                var entry = Assert.Single(configuration.Executors);
                Assert.Equal(expected, entry.CostBudget); Assert.Equal(3, entry.Slots);
            });
    }

    [Fact]
    public void EntryWithoutBudgetPreservesSlotOnlyAdmission() =>
        Assert.Null(new RemoteLaneExecutorEntry("executor", 60, Slots: 3).CostBudget);

    [Theory]
    [InlineData("null")]
    [InlineData("\"4\"")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1e999")]
    public void ExplicitInvalidBudgetDisablesConfiguration(string value) => WithFile(
        ",\"costBudget\":" + value, configuration =>
        {
            Assert.False(configuration.Enabled); Assert.Equal("invalid", configuration.DisabledReason);
            Assert.Empty(configuration.Executors);
        });

    private static void WithFile(string properties, Action<RemoteLaneExecutorConfiguration> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cost-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "executors.json");
            File.WriteAllText(path, "{\"executors\":[{\"id\":\"executor\",\"slots\":3" + properties + "}],\"lanes\":[\"lane\"]}");
            check(RemoteLaneExecutorConfiguration.Load(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
