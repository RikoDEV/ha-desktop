using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>
/// Searchable, type-filterable checklist of light/switch/cover/sensor
/// entities, used to pick which ones show up as flyout tiles instead of the
/// previous "first 8" default.
/// </summary>
public partial class EntityPickerWindow : Window
{
    private static readonly HashSet<string> PickableDomains = new()
    {
        "light", "switch", "cover", "sensor", "camera", "climate", "fan", "humidifier", "lawn_mower",
    };

    // All a row shows is a name and an icon — no need to hold every attribute of every entity in HA.
    private static readonly HashSet<string> RowAttributes = new() { "friendly_name", "device_class" };

    private readonly List<(string EntityId, string Domain, string Label, CheckBox CheckBox)> _rows = new();

    public EntityPickerWindow()
    {
        InitializeComponent();
        // Set after InitializeComponent, not via XAML SelectedIndex="0" — that fires
        // SelectionChanged during EndInit, before the window's name scope is fully
        // populated, so FindControl calls inside the handler throw.
        DomainFilterBox.SelectedIndex = 0;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var status = StatusText;
        var panel = RowsPanel;
        var client = HaSession.Client;

        if (client is null)
        {
            status.Text = Loc.Instance.Tr("Picker.NotConnected");
            return;
        }

        List<HaEntityState> states;
        try
        {
            states = await client.GetStatesAsync(IsPickable, RowAttributes);
        }
        catch (Exception ex)
        {
            status.Text = Loc.Instance.Tr("Picker.LoadError", ex.Message);
            return;
        }

        var selected = TileSelection.SelectedEntityIds(AppSettings.SelectedTiles);
        var controllable = states.OrderBy(HaEntityDisplay.LabelFor, StringComparer.OrdinalIgnoreCase);

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
                        new PathIcon { Data = TileIcons.GeometryFor(HaEntityDisplay.IconFor(state)), Width = 16, Height = 16 },
                        new TextBlock { Text = $"{label}  ({state.EntityId})" },
                    },
                },
                IsChecked = selected.Contains(state.EntityId),
            };
            _rows.Add((state.EntityId, state.Domain, label, checkBox));
            panel.Children.Add(checkBox);
        }

        status.Text = Loc.Instance.Tr("Picker.EntityCount", _rows.Count);
    }

    private static bool IsPickable(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot > 0 && PickableDomains.Contains(entityId[..dot]);
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnDomainFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var domainFilter = (DomainFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

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
        // Existing tiles keep their rename/icon overrides, and group tiles keep their still-ticked members.
        var chosenIds = _rows.Where(r => r.CheckBox.IsChecked == true).Select(r => r.EntityId).ToList();
        var chosen = TileSelection.Apply(AppSettings.SelectedTiles, chosenIds);

        await AppSettings.SetSelectedTilesAsync(chosen);
        Close();
    }
}
