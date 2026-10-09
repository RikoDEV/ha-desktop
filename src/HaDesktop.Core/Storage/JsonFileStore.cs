using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace HaDesktop.Core.Storage;

/// <summary>One preference object persisted as a JSON file under %LOCALAPPDATA%/HaDesktop. Not for secrets — those go through <see cref="SecretStore"/>.</summary>
public sealed class JsonFileStore<T>
{
    private readonly string _filePath;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T> _createDefault;

    internal JsonFileStore(string fileName, JsonTypeInfo<T> typeInfo, Func<T> createDefault)
    {
        _filePath = AppDataPaths.For(fileName);
        _typeInfo = typeInfo;
        _createDefault = createDefault;
    }

    public async Task SaveAsync(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(value, _typeInfo));
    }

    /// <summary>The saved value, or the default if nothing was saved yet or the file is unreadable.</summary>
    public async Task<T> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return _createDefault();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize(json, _typeInfo) ?? _createDefault();
        }
        catch (JsonException)
        {
            return _createDefault();
        }
    }
}

/// <summary>An on-by-default boolean setting, persisted as the presence of a marker file while it's switched off.</summary>
public sealed class DisabledFlagStore
{
    private readonly string _filePath;

    internal DisabledFlagStore(string fileName) => _filePath = AppDataPaths.For(fileName);

    public Task SaveAsync(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        if (!enabled)
            File.WriteAllText(_filePath, "1");
        else if (File.Exists(_filePath))
            File.Delete(_filePath);
        return Task.CompletedTask;
    }

    public Task<bool> LoadAsync() => Task.FromResult(!File.Exists(_filePath));
}

internal static class AppDataPaths
{
    public static string For(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaDesktop", fileName);
}
