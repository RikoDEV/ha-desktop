using System.Text.Json;

namespace HaDesktop.Core.Ha;

/// <summary>
/// Reads entity states straight off a <see cref="Utf8JsonReader"/> instead of going through a
/// JsonNode tree first. Home Assistant sends every state change in the whole house, and a
/// get_states response carries every entity it has — building a full DOM for all of that, only to
/// keep a handful of entities, was most of this app's managed allocation.
/// </summary>
internal static class HaStateParser
{
    /// <summary>
    /// With <paramref name="reader"/> on an object's StartObject token, advances to the value of the
    /// named property and returns true, or returns false (reader left on the EndObject) if it has none.
    /// </summary>
    public static bool TryMoveToProperty(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isMatch = reader.ValueTextEquals(name);
            reader.Read();
            if (isMatch) return true;
            reader.Skip();
        }

        return false;
    }

    /// <summary>
    /// Reads a JSON array of state objects (reader on its StartArray token). Entities rejected by
    /// <paramref name="entityFilter"/> are skipped without materializing anything but their id.
    /// </summary>
    public static List<HaEntityState> ReadStates(ref Utf8JsonReader reader, Func<string, bool>? entityFilter, IReadOnlySet<string>? attributeNames)
    {
        var states = new List<HaEntityState>();
        if (reader.TokenType != JsonTokenType.StartArray) return states;

        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            if (entityFilter is not null && (PeekEntityId(reader) is not { } entityId || !entityFilter(entityId)))
            {
                reader.Skip();
                continue;
            }

            if (ReadState(ref reader, attributeNames) is { } state)
                states.Add(state);
        }

        return states;
    }

    /// <summary>The entity_id of the state object <paramref name="reader"/> is on — takes the reader by value, so the caller's position is untouched.</summary>
    public static string? PeekEntityId(Utf8JsonReader reader) =>
        TryMoveToProperty(ref reader, "entity_id"u8) && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

    /// <summary>
    /// Reads one state object (reader on its StartObject token, left on its EndObject). Only the
    /// attributes named in <paramref name="attributeNames"/> are kept when it's given — a caller
    /// listing every entity just to show names doesn't need each one's full attribute set.
    /// </summary>
    public static HaEntityState? ReadState(ref Utf8JsonReader reader, IReadOnlySet<string>? attributeNames = null)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return null;

        string? entityId = null;
        string? state = null;
        var attributes = new Dictionary<string, object?>();

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("entity_id"u8))
            {
                reader.Read();
                entityId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("state"u8))
            {
                reader.Read();
                state = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("attributes"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject) ReadAttributes(ref reader, attributes, attributeNames);
                else reader.Skip();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (entityId is null || state is null) return null;
        return new HaEntityState { EntityId = entityId, State = state, Attributes = attributes };
    }

    private static void ReadAttributes(ref Utf8JsonReader reader, Dictionary<string, object?> attributes, IReadOnlySet<string>? attributeNames)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            reader.Read();

            if (attributeNames is not null && !attributeNames.Contains(key))
            {
                reader.Skip();
                continue;
            }

            attributes[key] = ReadAttributeValue(ref reader);
        }
    }

    /// <summary>
    /// Scalars map to string/double/bool/null. An array of only strings or only numbers becomes a
    /// string[] or double[] (hvac_modes, rgb_color and the like, read on every state change);
    /// anything else nested is kept as its raw JSON text.
    /// </summary>
    private static object? ReadAttributeValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return reader.GetString();
            case JsonTokenType.Number: return reader.GetDouble();
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Null: return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Array) return element.GetRawText();

        var length = element.GetArrayLength();
        if (length == 0) return Array.Empty<string>();

        var firstKind = element[0].ValueKind;
        if (firstKind is not (JsonValueKind.String or JsonValueKind.Number)) return element.GetRawText();

        foreach (var item in element.EnumerateArray())
            if (item.ValueKind != firstKind) return element.GetRawText();

        if (firstKind == JsonValueKind.String)
        {
            var strings = new string[length];
            var i = 0;
            foreach (var item in element.EnumerateArray()) strings[i++] = item.GetString()!;
            return strings;
        }

        var numbers = new double[length];
        var n = 0;
        foreach (var item in element.EnumerateArray()) numbers[n++] = item.GetDouble();
        return numbers;
    }
}
