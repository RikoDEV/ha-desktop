using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;

namespace HaDesktop.Tray;

/// <summary>
/// Searchable, type-filterable checklist of light/switch/cover/sensor
/// entities, used to pick which ones show up as flyout tiles instead of the
/// previous "first 8" default.
/// </summary>
public partial class EntityPickerWindow : Window
{
    private readonly List<(string EntityId, string Domain, string Label, CheckBox CheckBox)> _rows = new();

    public EntityPickerWindow()
    {
        InitializeComponent();
        // Set after InitializeComponent, not via XAML SelectedIndex="0" — that fires
        // SelectionChanged during EndInit, before the window's name scope is fully
        // populated, so FindControl calls inside the handler throw.
        this.FindControl<ComboBox>("DomainFilterBox")!.SelectedIndex = 0;
        _ = LoadAsync();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async Task LoadAsync()
    {
        var status = this.FindControl<TextBlock>("StatusText")!;
        var panel = this.FindControl<StackPanel>("RowsPanel")!;
        var client = AppSettings.Client;

        if (client is null)
        {
            status.Text = "Not connected.";
            return;
        }

        List<HaEntityState> states;
        try
        {
            states = await client.GetStatesAsync();
        }
        catch (Exception ex)
        {
            status.Text = $"Couldn't load entities: {ex.Message}";
            return;
        }

        var selected = new HashSet<string>(AppSettings.SelectedTiles.Select(t => t.EntityId));
        var controllable = states
            .Where(s => s.Domain is "light" or "switch" or "cover" or "sensor" or "camera")
            .OrderBy(HaEntityDisplay.LabelFor, StringComparer.OrdinalIgnoreCase);

        foreach (var state in controllable)
        {
            var label = HaEntityDisplay.LabelFor(state);
            var checkBox = new CheckBox
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new PathIcon { Data = Geometry.Parse(TileIcons.PathFor(HaEntityDisplay.IconFor(state))), Width = 16, Height = 16 },
                        new TextBlock { Text = $"{label}  ({state.EntityId})" },
                    },
                },
                IsChecked = selected.Contains(state.EntityId),
            };
            _rows.Add((state.EntityId, state.Domain, label, checkBox));
            panel.Children.Add(checkBox);
        }

        status.Text = $"{_rows.Count} entities";
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnDomainFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = this.FindControl<TextBox>("SearchBox")!.Text?.Trim() ?? string.Empty;
        var domainFilter = (this.FindControl<ComboBox>("DomainFilterBox")!.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        foreach (var (entityId, domain, label, checkBox) in _rows)
        {
            var matchesDomain = domainFilter.Length == 0 || domain == domainFilter;
            var matchesSearch = query.Length == 0
                || label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entityId.Contains(query, StringComparison.OrdinalIgnoreCase);

            checkBox.IsVisible = matchesDomain && matchesSearch;
        }
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        // Preserve any existing rename/icon overrides for entities that stay selected.
        var existingById = AppSettings.SelectedTiles.ToDictionary(t => t.EntityId);
        var chosen = _rows
            .Where(r => r.CheckBox.IsChecked == true)
            .Select(r => existingById.TryGetValue(r.EntityId, out var existing) ? existing : new TileConfig(r.EntityId))
            .ToList();

        await AppSettings.SetSelectedTilesAsync(chosen);
        Close();
    }
}
