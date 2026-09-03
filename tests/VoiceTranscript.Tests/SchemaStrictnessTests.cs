using System.Reflection;
using System.Text.Json.Nodes;

namespace VoiceTranscript.Tests;

/// <summary>
/// Every schema this application sends, checked against the rule the server enforces.
///
/// Structured output is requested with <c>"strict": true</c>, and strict decoding refuses a
/// schema in which "properties" holds a key that "required" does not list. Optional fields are
/// expressed by making the type nullable, not by leaving them out of "required".
///
/// This is a test rather than a review note because of how the failure behaves. The rejection is
/// survivable: the client catches it and retries the same instruction with no schema at all,
/// which is the correct fallback for a model that cannot do constrained decoding — and here it
/// silently hid a schema this application had written wrongly. Unconstrained, the model answered
/// in its own words: "konusmaci" where the parser reads "konusan", and no "yukumluluk" at all.
/// Seventy-nine ledger entries in a real archive were stored with an empty obligation and
/// nothing but a quote, and every screen showed them as promises that say nothing.
///
/// One schema was fixed by hand and a second, with the same key missing, went on being rejected
/// on every call for weeks. Fixing them one at a time is how that happens; this asks all of them
/// at once, including the ones written after today.
/// </summary>
public class SchemaStrictnessTests
{
    /// <summary>Every public static JsonNode named Schema in the analysis layer.</summary>
    public static TheoryData<string> Schemas()
    {
        var data = new TheoryData<string>();

        foreach (var (name, _) in Found()) data.Add(name);

        return data;
    }

    private static IEnumerable<(string Name, JsonNode Schema)> Found()
    {
        var assembly = typeof(VoiceTranscript.Core.Analysis.ExtractionPrompt).Assembly;

        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var property = type.GetProperty(
                "Schema", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (property?.PropertyType != typeof(JsonNode)) continue;
            if (property.GetValue(null) is not JsonNode schema) continue;

            yield return (type.Name, schema);
        }
    }

    [Fact]
    public void TheSchemasAreActuallyFound()
    {
        // A reflection test that finds nothing passes for the wrong reason.
        Assert.True(Found().Count() >= 5, string.Join(", ", Found().Select(f => f.Name)));
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void EveryPropertyIsRequired(string name)
    {
        var schema = Found().Single(f => f.Name == name).Schema;

        List<string> missing = [];
        Walk(schema, name, missing);

        Assert.True(missing.Count == 0,
            $"{name}: \"required\" bu anahtarları saymıyor — " + string.Join(", ", missing));
    }

    private static void Walk(JsonNode? node, string path, List<string> missing)
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]", missing);
                return;

            case not JsonObject:
                return;
        }

        var obj = (JsonObject)node;

        if (obj["properties"] is JsonObject properties)
        {
            var required = (obj["required"] as JsonArray)?
                .Select(r => r?.GetValue<string>())
                .Where(r => r is not null)
                .ToHashSet(StringComparer.Ordinal) ?? [];

            foreach (var (key, _) in properties)
            {
                if (!required.Contains(key)) missing.Add($"{path}.{key}");
            }
        }

        foreach (var (key, value) in obj) Walk(value, $"{path}.{key}", missing);
    }

    /// <summary>
    /// And "additionalProperties": false, which strict decoding also insists on for every object.
    /// </summary>
    [Theory]
    [MemberData(nameof(Schemas))]
    public void EveryObjectRefusesExtraKeys(string name)
    {
        var schema = Found().Single(f => f.Name == name).Schema;

        List<string> open = [];
        WalkObjects(schema, name, open);

        Assert.True(open.Count == 0,
            $"{name}: \"additionalProperties\": false eksik — " + string.Join(", ", open));
    }

    private static void WalkObjects(JsonNode? node, string path, List<string> open)
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) WalkObjects(array[i], $"{path}[{i}]", open);
                return;

            case not JsonObject:
                return;
        }

        var obj = (JsonObject)node;

        if (obj["properties"] is JsonObject && obj["additionalProperties"]?.GetValue<bool>() is not false)
            open.Add(path);

        foreach (var (key, value) in obj) WalkObjects(value, $"{path}.{key}", open);
    }
}
