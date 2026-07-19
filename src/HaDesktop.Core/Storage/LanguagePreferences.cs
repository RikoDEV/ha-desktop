namespace HaDesktop.Core.Storage;

public enum AppLanguage { English, Polish, German, French, Russian }

public sealed record LanguagePreferences(AppLanguage Language)
{
    public static LanguagePreferences Default { get; } = new(AppLanguage.English);
}
