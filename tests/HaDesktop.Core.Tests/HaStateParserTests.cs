using System.Buffers;
using System.Text;
using System.Text.Json;
using HaDesktop.Core.Ha;

namespace HaDesktop.Core.Tests;

public class HaStateParserTests
{
    private const string LightJson = """
        {
          "entity_id": "light.desk",
          "state": "on",
          "attributes": {
            "friendly_name": "Desk",
            "brightness": 128,
            "rgb_color": [255, 180, 90],
            "supported_color_modes": ["rgb", "color_temp"],
            "is_group": false,
            "effect": null,
            "mixed": [1, "two"],
            "nested": {"a": 1},
            "empty": []
          },
          "last_changed": "2026-10-09T10:00:00+00:00",
          "context": {"id": "abc", "parent_id": null}
        }
        """;

    private static Utf8JsonReader ReaderOn(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return reader;
    }

    [Fact]
    public void ReadState_MapsScalarsAndUniformArraysToClrTypes()
    {
        var reader = ReaderOn(LightJson);

        var state = HaStateParser.ReadState(ref reader)!;

        Assert.Equal("light.desk", state.EntityId);
        Assert.Equal("light", state.Domain);
        Assert.True(state.IsOn);
        Assert.Equal("Desk", state.Attributes["friendly_name"]);
        Assert.Equal(128.0, state.Attributes["brightness"]);
        Assert.Equal(new[] { 255.0, 180.0, 90.0 }, state.Attributes["rgb_color"]);
        Assert.Equal(new[] { "rgb", "color_temp" }, state.Attributes["supported_color_modes"]);
        Assert.Equal(false, state.Attributes["is_group"]);
        Assert.Null(state.Attributes["effect"]);
        Assert.Empty(Assert.IsType<string[]>(state.Attributes["empty"]));
    }

    [Fact]
    public void ReadState_KeepsNonUniformNestedValuesAsRawJson()
    {
        var reader = ReaderOn(LightJson);

        var state = HaStateParser.ReadState(ref reader)!;

        Assert.Equal("[1, \"two\"]", state.Attributes["mixed"]);
        Assert.Equal("{\"a\": 1}", state.Attributes["nested"]);
    }

    [Fact]
    public void ReadState_LeavesTheReaderAtTheEndOfTheObject()
    {
        var reader = ReaderOn(LightJson);

        HaStateParser.ReadState(ref reader);

        Assert.Equal(JsonTokenType.EndObject, reader.TokenType);
        Assert.False(reader.Read());
    }

    [Fact]
    public void ReadState_KeepsOnlyRequestedAttributes()
    {
        var reader = ReaderOn(LightJson);

        var state = HaStateParser.ReadState(ref reader, new HashSet<string> { "friendly_name" })!;

        Assert.Equal(new[] { "friendly_name" }, state.Attributes.Keys);
    }

    [Fact]
    public void ReadState_DoesNotDependOnPropertyOrder()
    {
        var reader = ReaderOn("""{"attributes": {"friendly_name": "Fan"}, "context": {}, "state": "off", "entity_id": "fan.attic"}""");

        var state = HaStateParser.ReadState(ref reader)!;

        Assert.Equal("fan.attic", state.EntityId);
        Assert.Equal("off", state.State);
        Assert.Equal("Fan", state.Attributes["friendly_name"]);
    }

    [Theory]
    [InlineData("""{"state": "on"}""")]
    [InlineData("""{"entity_id": "light.x"}""")]
    [InlineData("""{"entity_id": 5, "state": "on"}""")]
    public void ReadState_ReturnsNullWithoutAnIdOrState(string json)
    {
        var reader = ReaderOn(json);

        Assert.Null(HaStateParser.ReadState(ref reader));
    }

    [Fact]
    public void ReadStates_SkipsEntitiesTheFilterRejects()
    {
        var reader = ReaderOn("""
            [
              {"entity_id": "light.a", "state": "on", "attributes": {"brightness": 10}},
              {"entity_id": "sensor.b", "state": "21.5", "attributes": {"huge": {"deep": [1, 2, {"x": [3]}]}}},
              {"state": "orphan"},
              {"entity_id": "light.c", "state": "off", "attributes": {}}
            ]
            """);

        var states = HaStateParser.ReadStates(ref reader, id => id.StartsWith("light."), null);

        Assert.Equal(new[] { "light.a", "light.c" }, states.Select(s => s.EntityId));
        Assert.Equal(JsonTokenType.EndArray, reader.TokenType);
    }

    [Fact]
    public void ReadStates_WithoutAFilterReturnsEverythingWellFormed()
    {
        var reader = ReaderOn("""[{"entity_id": "light.a", "state": "on"}, {"state": "orphan"}, {"entity_id": "switch.b", "state": "off"}]""");

        var states = HaStateParser.ReadStates(ref reader, null, null);

        Assert.Equal(new[] { "light.a", "switch.b" }, states.Select(s => s.EntityId));
    }

    [Fact]
    public void TryMoveToProperty_FindsNestedValuesAndReportsMissingOnes()
    {
        var reader = ReaderOn("""{"id": 7, "event": {"data": {"entity_id": "light.a", "new_state": null}}, "type": "event"}""");

        Assert.True(HaStateParser.TryMoveToProperty(ref reader, "event"u8));
        Assert.True(HaStateParser.TryMoveToProperty(ref reader, "data"u8));
        Assert.Equal("light.a", HaStateParser.PeekEntityId(reader));
        Assert.True(HaStateParser.TryMoveToProperty(ref reader, "new_state"u8));
        Assert.Equal(JsonTokenType.Null, reader.TokenType);

        var other = ReaderOn("""{"id": 7}""");
        Assert.False(HaStateParser.TryMoveToProperty(ref other, "event"u8));
    }

    [Fact]
    public void ReadStates_WorksAcrossReceiveBufferChunkBoundaries()
    {
        // Big enough to span several 32 KB chunks, with entities straddling the seams.
        var json = new StringBuilder("[");
        const int count = 2000;
        for (var i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($$$"""{"entity_id": "sensor.s{{{i}}}", "state": "{{{i}}}", "attributes": {"friendly_name": "Sensor número {{{i}}}", "options": ["a", "b", "c"]}}""");
        }
        json.Append(']');
        var bytes = Encoding.UTF8.GetBytes(json.ToString());

        var buffer = new ReceiveBuffer();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var memory = buffer.GetMemory();
            var chunk = Math.Min(Math.Min(memory.Length, 5000), bytes.Length - offset); // odd-sized writes, like real socket frames
            bytes.AsSpan(offset, chunk).CopyTo(memory.Span);
            buffer.Advance(chunk);
            offset += chunk;
        }

        var sequence = buffer.AsSequence();
        Assert.False(sequence.IsSingleSegment);
        Assert.Equal(bytes, sequence.ToArray());

        var reader = new Utf8JsonReader(sequence);
        reader.Read();
        var states = HaStateParser.ReadStates(ref reader, id => !id.EndsWith('7'), null);

        Assert.Equal(count - count / 10, states.Count);
        Assert.Equal("Sensor número 1999", states[^1].Attributes["friendly_name"]);
        Assert.Equal(new[] { "a", "b", "c" }, states[^1].Attributes["options"]);
    }

    [Fact]
    public void ReceiveBuffer_ResetReusesTheFirstChunk()
    {
        var buffer = new ReceiveBuffer();
        "first"u8.CopyTo(buffer.GetMemory().Span);
        buffer.Advance(5);

        buffer.Reset();
        "second"u8.CopyTo(buffer.GetMemory().Span);
        buffer.Advance(6);

        Assert.Equal("second", Encoding.UTF8.GetString(buffer.AsSequence().ToArray()));
    }
}
