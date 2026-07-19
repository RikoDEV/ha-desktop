using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray.Localization;

/// <summary>
/// Loads embedded per-language JSON dictionaries and exposes translated strings to XAML (via
/// <see cref="TrExtension"/>) and to code-behind via <see cref="Tr"/>. Falls back to English,
/// then to the raw key itself, so a missing translation never blanks the UI.
/// </summary>
public sealed class Loc
{
    // Declared before Instance: static field initializers run in source order, and the Loc()
    // constructor (triggered by Instance's initializer) reads CultureNames — if CultureNames
    // were declared after Instance, it would still be null when the constructor runs.
    private static readonly Dictionary<AppLanguage, string> CultureNames = new()
    {
        [AppLanguage.English] = "en-US",
        [AppLanguage.Polish] = "pl-PL",
        [AppLanguage.German] = "de-DE",
        [AppLanguage.French] = "fr-FR",
        [AppLanguage.Russian] = "ru-RU",
    };

    public static Loc Instance { get; } = new();

    private readonly Dictionary<AppLanguage, Dictionary<string, string>> _strings = new();

    /// <summary>Raised after <see cref="Current"/> changes. Drives both <see cref="TrExtension"/>'s XAML bindings and UI surfaces (native tray menus) that can't use a binding at all.</summary>
    public event Action? LanguageChanged;

    public AppLanguage Current { get; private set; } = AppLanguage.English;

    private Loc()
    {
        foreach (var language in CultureNames.Keys)
            _strings[language] = LoadEmbedded(language);
    }

    private static Dictionary<string, string> LoadEmbedded(AppLanguage language)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"HaDesktop.Tray.Localization.Strings.{language.ToString().ToLowerInvariant()}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return new Dictionary<string, string>();

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    public void SetLanguage(AppLanguage language)
    {
        if (Current == language) return;
        Current = language;

        var culture = CultureInfo.GetCultureInfo(CultureNames[language]);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        LanguageChanged?.Invoke();
    }

    /// <summary>Looks up a key for the current language, falling back to English, then the key itself.</summary>
    public string Tr(string key)
    {
        if (_strings.TryGetValue(Current, out var current) && current.TryGetValue(key, out var value))
            return value;
        if (_strings.TryGetValue(AppLanguage.English, out var en) && en.TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    public string Tr(string key, params object?[] args) => string.Format(Tr(key), args);
}
