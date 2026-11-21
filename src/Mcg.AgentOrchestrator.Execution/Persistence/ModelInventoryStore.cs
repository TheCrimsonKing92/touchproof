using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ModelInventoryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ModelInventory Load(string path) => Read(path, ModelInventory.Empty);
    public static void Save(string path, ModelInventory inventory) => Write(path, inventory);
    public static IReadOnlyList<ModelDeclaration> LoadDeclarations(string path) =>
        Read<IReadOnlyList<ModelDeclaration>>(path, []);

    public static void UpdateDeclarations(
        string path, Func<IReadOnlyList<ModelDeclaration>, IReadOnlyList<ModelDeclaration>> update)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        Write(path, update(LoadDeclarations(path)));
    }

    private static T Read<T>(string path, T missing)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(file, Options)
                ?? throw new JsonException($"'{path}' contains no inventory data.");
        }
        catch (FileNotFoundException) { return missing; }
        catch (DirectoryNotFoundException) { return missing; }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
