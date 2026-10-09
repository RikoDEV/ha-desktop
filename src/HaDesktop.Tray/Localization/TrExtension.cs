using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
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
        // target control) alive forever — stop listening while the owning control is out of the
        // visual tree (window closed, tile dropped), and catch up if it's ever put back.
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: Control control })
        {
            control.AttachedToVisualTree += (_, _) => node.Listen();
            control.DetachedFromVisualTree += (_, _) => node.StopListening();
        }

        // An observable rather than a Binding to a property path: that kind of binding finds its
        // property by reflection, which trimming breaks (the text silently comes out empty).
        return node.ToBinding();
    }

    private sealed class LocNode : IObservable<string>
    {
        private readonly string _key;
        private readonly List<IObserver<string>> _observers = new();
        private bool _listening;

        public LocNode(string key)
        {
            _key = key;
            Listen();
        }

        public void Listen()
        {
            if (_listening) return;
            _listening = true;
            Loc.Instance.LanguageChanged += Publish;
            Publish(); // the language may have changed while this wasn't listening
        }

        public void StopListening()
        {
            if (!_listening) return;
            _listening = false;
            Loc.Instance.LanguageChanged -= Publish;
        }

        public IDisposable Subscribe(IObserver<string> observer)
        {
            _observers.Add(observer);
            observer.OnNext(Loc.Instance.Tr(_key));
            return new Subscription(this, observer);
        }

        private void Publish()
        {
            if (_observers.Count == 0) return;

            var text = Loc.Instance.Tr(_key);
            foreach (var observer in _observers.ToArray())
                observer.OnNext(text);
        }

        private sealed class Subscription(LocNode node, IObserver<string> observer) : IDisposable
        {
            public void Dispose() => node._observers.Remove(observer);
        }
    }
}
