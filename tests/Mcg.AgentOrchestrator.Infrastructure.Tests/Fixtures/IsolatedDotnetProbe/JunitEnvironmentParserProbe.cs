using System.Text.Json;
using System.Xml.Linq;

// Directly executable parser fixture; configuration and observations belong to its copied apphost.
internal static class JunitEnvironmentParserProbe
{
    internal static int Run(string[] arguments)
    {
        if (arguments.Length != 5 || arguments[1] != "junit" || arguments[3] != "--tool")
            throw new ArgumentException("Expected parse junit <report> --tool <name>.");
        using var document = JsonDocument.Parse(File.ReadAllText(Environment.ProcessPath! + ".json"));
        var config = document.RootElement;
        var value = Environment.GetEnvironmentVariable(config.GetProperty("variable").GetString()!);
        File.WriteAllText(config.GetProperty("observation").GetString()!, JsonSerializer.Serialize(new
            { value, mode = "parser", processId = Environment.ProcessId }));
        if (value is not null) throw new InvalidOperationException("Parser received a check-local variable.");
        var diagnostic = config.GetProperty("diagnostic").GetString();
        if (config.GetProperty("failParser").GetBoolean())
        {
            Console.Error.WriteLine(diagnostic);
            return 8;
        }

        var report = XDocument.Load(arguments[2]);
        var rows = report.Root!.Elements("testcase").Select(test =>
        {
            var file = (string?)test.Attribute("file");
            var name = (string?)test.Attribute("name");
            var row = new Dictionary<string, object?>
                { ["testId"] = file + "::" + name, ["filePath"] = file, ["name"] = name, ["status"] = "passed" };
            if (diagnostic is not null) row["message"] = diagnostic;
            return row;
        }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            reportFormat = "CTRF", schemaVersion = "mcg-test-result/1",
            results = new { tool = new { name = arguments[4] }, tests = rows }
        }));
        return 0;
    }
}
