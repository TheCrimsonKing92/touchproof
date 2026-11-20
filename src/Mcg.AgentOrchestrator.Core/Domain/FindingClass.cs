using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(FindingClassJsonConverter))]
public enum FindingClass
{
    None,
    DispositiveMisclassification
}

public static class FindingClassExtensions
{
    extension(FindingClass findingClass)
    {
        public bool IsRegisteredBlocking => findingClass == FindingClass.DispositiveMisclassification;
    }
}

public sealed class FindingClassJsonConverter : JsonConverter<FindingClass>
{
    public override FindingClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (reader.TokenType == JsonTokenType.String ? reader.GetString() : null) switch
        {
            "none" => FindingClass.None,
            "dispositive-misclassification" => FindingClass.DispositiveMisclassification,
            _ => throw new JsonException("class_id must be 'none' or 'dispositive-misclassification'.")
        };

    public override void Write(Utf8JsonWriter writer, FindingClass value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            FindingClass.None => "none",
            FindingClass.DispositiveMisclassification => "dispositive-misclassification",
            _ => throw new JsonException($"Unknown finding class: {value}.")
        });
}
