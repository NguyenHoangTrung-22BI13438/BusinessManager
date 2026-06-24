using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagFlowApi.Models;

[JsonConverter(typeof(BBoxConverter))]
public record BBox(int X1, int Y1, int X2, int Y2);

[JsonConverter(typeof(LayoutCategoryConverter))]
public enum LayoutCategory
{
    Title, SectionHeader, Text, ListItem, Table, Formula,
    Picture, Caption, Footnote, PageHeader, PageFooter, Unknown
}

public record LayoutElement
{
    [JsonPropertyName("bbox")]
    public BBox Bbox { get; init; } = new(0, 0, 0, 0);

    [JsonPropertyName("category")]
    public LayoutCategory Category { get; init; } = LayoutCategory.Unknown;

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

//Converters ─────────────────────────────────────────────────────────────

internal sealed class BBoxConverter : JsonConverter<BBox>
{
    public override BBox Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray)
            throw new JsonException("bbox must be an array [x1,y1,x2,y2]");
        var n = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!r.Read() || r.TokenType != JsonTokenType.Number)
                throw new JsonException("invalid bbox element");
            n[i] = r.GetInt32();
        }
        if (!r.Read() || r.TokenType != JsonTokenType.EndArray)
            throw new JsonException("expected end of bbox array");
        return new BBox(n[0], n[1], n[2], n[3]);
    }

    public override void Write(Utf8JsonWriter w, BBox v, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        w.WriteNumberValue(v.X1); w.WriteNumberValue(v.Y1);
        w.WriteNumberValue(v.X2); w.WriteNumberValue(v.Y2);
        w.WriteEndArray();
    }
}

internal sealed class LayoutCategoryConverter : JsonConverter<LayoutCategory>
{
    private static readonly Dictionary<string, LayoutCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Title"] = LayoutCategory.Title,
        ["Section-header"] = LayoutCategory.SectionHeader,
        ["Text"] = LayoutCategory.Text,
        ["List-item"] = LayoutCategory.ListItem,
        ["Table"] = LayoutCategory.Table,
        ["Formula"] = LayoutCategory.Formula,
        ["Picture"] = LayoutCategory.Picture,
        ["Caption"] = LayoutCategory.Caption,
        ["Footnote"] = LayoutCategory.Footnote,
        ["Page-header"] = LayoutCategory.PageHeader,
        ["Page-footer"] = LayoutCategory.PageFooter
    };

    private static readonly Dictionary<LayoutCategory, string> Reverse =
        Map.ToDictionary(kv => kv.Value, kv => kv.Key);

    public override LayoutCategory Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var s = r.GetString();
        return s != null && Map.TryGetValue(s, out var c) ? c : LayoutCategory.Unknown;
    }

    public override void Write(Utf8JsonWriter w, LayoutCategory v, JsonSerializerOptions o)
        => w.WriteStringValue(Reverse.TryGetValue(v, out var s) ? s : "Unknown");
}