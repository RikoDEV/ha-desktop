using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace HaDesktop.Tray.Localization;

/// <summary>XAML markup extension for static UI text: {loc:Tr SomeKey}. Rebinds automatically when the language changes.</summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; }

    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var node = new LocNode(Key);

        // Loc.Instance's LanguageChanged event would otherwise keep every node (and its
        // target control) alive forever — unsubscribe once the owning control leaves the
        // visual tree (window closed, tile rebuilt) so this doesn't leak.
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: Control control })
        {
            control.DetachedFromVisualTree += OnDetached;
            void OnDetached(object? s, Avalonia.VisualTreeAttachmentEventArgs e)
            {
                control.DetachedFromVisualTree -= OnDetached;
                node.Dispose();
            }
        }

        return new Binding(nameof(LocNode.Value)) { Source = node, Mode = BindingMode.OneWay };
    }

    /// <summary>Plain observable property, not an indexer — avoids relying on Avalonia's binding engine supporting the WPF-style "Item[]" indexer-refresh convention.</summary>
    private sealed class LocNode : INotifyPropertyChanged, IDisposable
    {
        private readonly string _key;

        public LocNode(string key)
        {
            _key = key;
            Loc.Instance.LanguageChanged += OnLanguageChanged;
        }

        public string Value => Loc.Instance.Tr(_key);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnLanguageChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));

        public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }
}
