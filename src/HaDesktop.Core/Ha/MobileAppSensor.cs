using System.Text.Json.Nodes;

namespace HaDesktop.Core.Ha;

/// <summary>One sensor as HA's mobile_app webhook API expects it — see register_sensor / update_sensor_states payloads.</summary>
public sealed record MobileAppSensor(
    string UniqueId,
    string Name,
    object State,
    string? Icon = null,
    string? DeviceClass = null,
    string? UnitOfMeasurement = null,
    string? StateClass = null)
{
    public JsonObject ToRegisterPayload()
    {
        var obj = new JsonObject
        {
            ["name"] = Name,
            ["state"] = StateNode(),
            ["type"] = "sensor",
            ["unique_id"] = UniqueId,
        };
        if (Icon is not null) obj["icon"] = Icon;
        if (DeviceClass is not null) obj["device_class"] = DeviceClass;
        if (UnitOfMeasurement is not null) obj["unit_of_measurement"] = UnitOfMeasurement;
        if (StateClass is not null) obj["state_class"] = StateClass;
        return obj;
    }

    // Typed overloads rather than JsonValue.Create<object>, which serializes by reflection (and so breaks under trimming).
    private JsonNode StateNode() => State switch
    {
        string text => JsonValue.Create(text),
        double number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        bool flag => JsonValue.Create(flag),
        _ => JsonValue.Create(State.ToString() ?? string.Empty),
    };

    public JsonObject ToUpdatePayload()
    {
        var obj = new JsonObject
        {
            ["unique_id"] = UniqueId,
            ["state"] = StateNode(),
            ["type"] = "sensor",
        };
        if (Icon is not null) obj["icon"] = Icon;
        return obj;
    }
}
