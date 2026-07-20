using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using HaDesktop.Core.Ha;
using HaDesktop.Core.Storage;
using HaDesktop.Tray.Localization;

namespace HaDesktop.Tray;

/// <summary>
/// Visual, drag-and-drop editor for the flyout's tile grid, embedded directly in the Tiles settings
/// page — reorder by dragging, resize via a per-tile Small/Wide toggle, merge two tiles by dropping
/// one squarely onto another to create a 2x2 Group tile (up to 4 entities stacked into one slot,
/// Windows-Start-Menu-folder-tile style), and drag a tile back out of a Group the same way Start
/// Menu folder tiles work. A dashed "+" ghost tile always sits at the end of the grid in place of a
/// separate "Add Tiles" button — clicking it opens the entity picker.
///
/// Same-window pointer-capture dragging is used instead of Avalonia's cross-control DragDrop
/// class — that's built for payloads that might cross window/process boundaries (e.g. dropping a
/// file), which is more machinery than same-window grid rearrangement needs, and makes continuous
/// ghost-follows-cursor feedback awkward to skin. Tiles here are lightweight read-only preview
/// cards (icon + label), not the fully live/interactive tiles the flyout renders — the editor is
/// for arranging layout, not for actually toggling devices.
///
/// While dragging, other tiles live-preview the reorder by animating into their would-be spot
/// (Windows Start Menu's "make room" effect), and a tile hovered squarely enough to merge with
/// gets an animated accent highlight instead — both driven by the same Transitions already
/// attached to every card for its Canvas position, so a settle-into-place after a drop animates
/// the same way a live preview shift does.
/// </summary>
public partial class TileLayoutEditor : UserControl
{
    private const string AddTileKey = "__add_tile__";

    private const double CellWidth = 108;
    private const double CellHeight = 92;
    private const double TileMargin = 4;

    // A drag has to move at least this far before it "counts" — otherwise a plain click (on the
    // size toggle, edit, or remove buttons layered on top of a card) would register as a zero-
    // distance drag-and-drop first.
    private const double DragThreshold = 6;

    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(180);
    private static readonly IBrush NormalBorderBrush = new SolidColorBrush(Colors.Gray);
    private static readonly IBrush NormalBackgroundBrush = new SolidColorBrush(Color.Parse("#33808080"));
    private static readonly IBrush MergeBorderBrush = new SolidColorBrush(Color.Parse("#0A84FF"));
    private static readonly IBrush MergeBackgroundBrush = new SolidColorBrush(Color.Parse("#330A84FF"));
    private static readonly IBrush AddTileBorderBrush = new SolidColorBrush(Color.Parse("#66808080"));
    private static readonly IBrush AddTileBackgroundBrush = new SolidColorBrush(Color.Parse("#11808080"));

    private readonly Dictionary<string, HaEntityState> _statesByEntityId = new();
    private readonly Dictionary<string, Border> _cardsByKey = new();
    private readonly Dictionary<string, TileSize> _cardSizeByKey = new();
    private List<TileConfig> _currentConfigs = new();
    private string? _mergeHighlightKey;

    private Border? _draggingCard;
    private TileConfig? _draggingConfig;
    private Point _dragStartPointerPos;
    private double _dragStartLeft;
    private double _dragStartTop;

    // A quadrant drag starts life pinned inside its GroupTile card; it only "detaches" into its
    // own floating ghost (and thus counts as pulling it out of the group) once the pointer has
    // moved past the drag threshold — a plain tap on a quadrant does nothing, rather than yanking
    // the entity out for a one-pixel jitter.
    private string? _quadrantDragGroupId;
    private string? _quadrantDragEntityId;
    private Point _quadrantDragStartPointerPos;
    private double _quadrantDragOriginLeft;
    private double _quadrantDragOriginTop;
    private Border? _quadrantGhost;

    public TileLayoutEditor()
    {
        InitializeComponent();
        AppSettings.ConnectionChanged += OnConnectionChanged;
        Loc.Instance.LanguageChanged += OnConnectionChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            AppSettings.ConnectionChanged -= OnConnectionChanged;
            Loc.Instance.LanguageChanged -= OnConnectionChanged;
        };
        _ = RefreshAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnConnectionChanged() => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    /// <summary>
    /// True whenever a drag (whole-tile or a quadrant pulled out of a Group) is in progress. A
    /// refresh has to defer entirely during this window: it would otherwise reset the actively-
    /// dragged card's Canvas position out from under the pointer, and — if the reuse-vs-recreate
    /// logic below decides to rebuild that card fresh — leave the *old* Border instance (which
    /// still holds pointer capture and the drag's PointerReleased handler) detached from the visual
    /// tree with no way to ever receive its release event, stuck permanently mid-drag-styled
    /// (dimmed, elevated, unresponsive) until the window is closed and reopened.
    /// </summary>
    private bool IsDragActive => _draggingCard is not null || _quadrantGhost is not null;

    private int _refreshToken;

    private async Task RefreshAsync()
    {
        // AppSettings.ConnectionChanged fires for any settings change anywhere in the app — not
        // just tile edits — so overlapping calls here are routine, not exceptional: a drop's own
        // direct refresh call can easily still be awaiting HA's state fetch when an unrelated
        // change (or another drop) posts a second one. An older call resuming after a newer one
        // already rebuilt the canvas would otherwise re-add/duplicate cards.
        if (IsDragActive) return;
        var myToken = ++_refreshToken;

        _statesByEntityId.Clear();
        if (AppSettings.Client is { } client)
        {
            try
            {
                foreach (var state in await client.GetStatesAsync())
                    _statesByEntityId[state.EntityId] = state;
            }
            catch { /* editor still usable — labels fall back to raw entity ids below */ }
        }

        if (myToken != _refreshToken || IsDragActive) return; // superseded, or a drag started while we were awaiting

        _currentConfigs = TileLayoutCompactor.Compact(AppSettings.SelectedTiles);
        _mergeHighlightKey = null;

        // The "+" add-tile card always renders as one more Small slot right after the real tiles —
        // reusing Compact to find its cell means it never overlaps whatever the real tiles' own
        // Wide/Group spans leave behind.
        var withAddTile = TileLayoutCompactor.Compact(_currentConfigs.Append(new TileConfig(AddTileKey)).ToList());

        var canvas = this.FindControl<Canvas>("LayoutCanvas")!;
        var stillPresent = new HashSet<string>();
        var maxRow = 0;
        var maxCol = TileLayoutCompactor.ColumnCount;

        foreach (var config in withAddTile)
        {
            stillPresent.Add(config.EntityId);
            var colSpan = TileLayoutCompactor.ColSpanFor(config.Size);
            var rowSpan = TileLayoutCompactor.RowSpanFor(config.Size);
            var left = config.Col * CellWidth + TileMargin;
            var top = config.Row * CellHeight + TileMargin;

            // Reusing the same Border instance (rather than tearing it down and rebuilding) is
            // what lets its position Transitions actually animate it sliding to the new spot —
            // a freshly-created control has no "from" position to animate from. Tiles that
            // changed size/type (Small <-> Group) are recreated instead, since their whole visual
            // shape changed anyway, not just their position.
            if (_cardsByKey.TryGetValue(config.EntityId, out var existing) && _cardSizeByKey[config.EntityId] == config.Size)
            {
                existing.Child = BuildCardContent(config);
                Canvas.SetLeft(existing, left);
                Canvas.SetTop(existing, top);
                // Belt-and-suspenders: a card should never legitimately reach a settle-refresh
                // still dimmed/elevated from a drag, but if some other edge case ever leaves one
                // that way, this makes the next refresh self-heal it instead of leaving it stuck
                // until the window is closed and reopened.
                existing.ZIndex = 0;
                existing.Opacity = 1;
            }
            else
            {
                if (existing is not null)
                {
                    canvas.Children.Remove(existing);
                    _cardsByKey.Remove(config.EntityId);
                }

                var card = BuildCard(config);
                Canvas.SetLeft(card, left);
                Canvas.SetTop(card, top);
                canvas.Children.Add(card);
                _cardsByKey[config.EntityId] = card;
                _cardSizeByKey[config.EntityId] = config.Size;
            }

            maxRow = Math.Max(maxRow, config.Row + rowSpan);
            maxCol = Math.Max(maxCol, config.Col + colSpan);
        }

        foreach (var staleKey in _cardsByKey.Keys.Where(k => !stillPresent.Contains(k)).ToList())
        {
            canvas.Children.Remove(_cardsByKey[staleKey]);
            _cardsByKey.Remove(staleKey);
            _cardSizeByKey.Remove(staleKey);
        }

        canvas.Width = maxCol * CellWidth;
        canvas.Height = (maxRow + 1) * CellHeight; // one extra row of headroom to drop a new bottom row into
    }

    private Control BuildCardContent(TileConfig config) => config.EntityId switch
    {
        AddTileKey => BuildAddTileContent(),
        _ => config.Size == TileSize.Group ? BuildGroupPreview(config) : BuildSinglePreview(config),
    };

    private Border BuildCard(TileConfig config)
    {
        var isAddTile = config.EntityId == AddTileKey;
        var colSpan = TileLayoutCompactor.ColSpanFor(config.Size);
        var rowSpan = TileLayoutCompactor.RowSpanFor(config.Size);

        var root = new Border
        {
            Width = colSpan * CellWidth - 2 * TileMargin,
            Height = rowSpan * CellHeight - 2 * TileMargin,
            CornerRadius = new CornerRadius(6),
            Background = isAddTile ? AddTileBackgroundBrush : NormalBackgroundBrush,
            BorderBrush = isAddTile ? AddTileBorderBrush : NormalBorderBrush,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(isAddTile ? StandardCursorType.Hand : StandardCursorType.SizeAll),
            Child = BuildCardContent(config),
            Transitions = BuildCardTransitions(),
        };

        if (isAddTile)
        {
            root.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                new EntityPickerWindow().Show();
            };
        }
        else if (config.Size != TileSize.Group)
        {
            // Group cards aren't draggable as a whole (there's nothing to merge a 2x2 tile into) —
            // only their individual quadrants (handled in BuildGroupQuadrant) can be dragged, which
            // pulls that one entity back out.
            AttachDragHandlers(root, config);
        }

        return root;
    }

    private static Control BuildAddTileContent()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = "+", FontSize = 22, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.Instance.Tr("Tiles.ChooseTiles"),
            FontSize = 9, Opacity = 0.6, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 80, HorizontalAlignment = HorizontalAlignment.Center,
        });
        return stack;
    }

    private static Transitions BuildCardTransitions() => new()
    {
        new DoubleTransition { Property = Canvas.LeftProperty, Duration = AnimationDuration, Easing = new CubicEaseOut() },
        new DoubleTransition { Property = Canvas.TopProperty, Duration = AnimationDuration, Easing = new CubicEaseOut() },
        new BrushTransition { Property = Border.BackgroundProperty, Duration = AnimationDuration },
        new BrushTransition { Property = Border.BorderBrushProperty, Duration = AnimationDuration },
        new ThicknessTransition { Property = Border.BorderThicknessProperty, Duration = AnimationDuration },
    };

    private Control BuildSinglePreview(TileConfig config)
    {
        _statesByEntityId.TryGetValue(config.EntityId, out var state);
        var iconKey = config.CustomIcon ?? (state is not null ? HaEntityDisplay.IconFor(state) : "circle");
        var label = config.CustomLabel ?? (state is not null ? HaEntityDisplay.LabelFor(state) : config.EntityId);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Margin = new Thickness(4, 4, 4, 26),
        };
        stack.Children.Add(new PathIcon { Data = Geometry.Parse(TileIcons.PathFor(iconKey)), Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 96, HorizontalAlignment = HorizontalAlignment.Center });

        var panel = new Panel();
        panel.Children.Add(stack);

        var sizeButton = new Button
        {
            Content = config.Size == TileSize.Wide ? "W" : "S",
            Width = 24, Height = 20, Padding = new Thickness(0), FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 26, 2),
        };
        ToolTip.SetTip(sizeButton, Loc.Instance.Tr("Tiles.ToggleSizeTooltip"));
        sizeButton.Click += async (_, e) =>
        {
            e.Handled = true;
            await AppSettings.SetTileSizeAsync(config.EntityId, config.Size == TileSize.Wide ? TileSize.Small : TileSize.Wide);
        };
        panel.Children.Add(sizeButton);

        var editButton = new Button
        {
            Content = new PathIcon { Data = Geometry.Parse(TileIcons.PathFor("pencil")), Width = 11, Height = 11 },
            Width = 20, Height = 20, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
        };
        ToolTip.SetTip(editButton, Loc.Instance.Tr("Tiles.RenameTooltip"));
        var defaultIconKey = state is not null ? HaEntityDisplay.IconFor(state) : "circle";
        var defaultLabel = state is not null ? HaEntityDisplay.LabelFor(state) : config.EntityId;
        editButton.Click += (_, e) =>
        {
            e.Handled = true;
            TileEditFlyout.Show(editButton, config.CustomLabel, config.CustomIcon, defaultLabel, defaultIconKey,
                async (newLabel, newIcon) => await AppSettings.UpdateTileAsync(config.EntityId, newLabel, newIcon));
        };
        panel.Children.Add(editButton);

        var removeButton = new Button
        {
            Content = "✕",
            Width = 20, Height = 20, Padding = new Thickness(0), FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
        };
        ToolTip.SetTip(removeButton, Loc.Instance.Tr("TileEditor.RemoveTooltip"));
        removeButton.Click += async (_, e) =>
        {
            e.Handled = true;
            await RemoveTileAsync(config.EntityId);
        };
        panel.Children.Add(removeButton);

        return panel;
    }

    private Control BuildGroupPreview(TileConfig config)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("*,*") };
        var ids = config.GroupEntityIds ?? new List<string>();

        for (var i = 0; i < 4; i++)
        {
            Control cell = i < ids.Count ? BuildGroupQuadrant(config.EntityId, ids[i]) : new Border { Background = Brushes.Transparent };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private Control BuildGroupQuadrant(string groupId, string entityId)
    {
        _statesByEntityId.TryGetValue(entityId, out var state);
        var iconKey = state is not null ? HaEntityDisplay.IconFor(state) : "circle";
        var label = state is not null ? HaEntityDisplay.LabelFor(state) : entityId;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(2),
        };
        stack.Children.Add(new PathIcon { Data = Geometry.Parse(TileIcons.PathFor(iconKey)), Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 8, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 64, HorizontalAlignment = HorizontalAlignment.Center });

        var quadrant = new Border
        {
            Margin = new Thickness(1),
            Background = new SolidColorBrush(Color.Parse("#22808080")),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack,
        };

        AttachQuadrantDragHandlers(quadrant, groupId, entityId);
        return quadrant;
    }

    private void AttachDragHandlers(Border root, TileConfig config)
    {
        root.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed) return;

            _draggingCard = root;
            _draggingConfig = config;
            var canvas = this.FindControl<Canvas>("LayoutCanvas")!;
            _dragStartPointerPos = e.GetPosition(canvas);
            _dragStartLeft = Canvas.GetLeft(root);
            _dragStartTop = Canvas.GetTop(root);

            // The dragged card itself tracks the pointer 1:1 — its own Transitions are cleared for
            // the duration of the drag so it doesn't lag behind the cursor with an eased animation;
            // every *other* card keeps its Transitions, which is what makes the live "make room"
            // reflow preview below animate smoothly instead of jumping.
            root.Transitions = null;
            e.Pointer.Capture(root);
            root.ZIndex = 100;
            root.Opacity = 0.75;
        };

        root.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(_draggingCard, root) || _draggingConfig is null) return;

            var canvas = this.FindControl<Canvas>("LayoutCanvas")!;
            var pos = e.GetPosition(canvas);
            var delta = pos - _dragStartPointerPos;
            Canvas.SetLeft(root, _dragStartLeft + delta.X);
            Canvas.SetTop(root, _dragStartTop + delta.Y);

            UpdateDragPreview(_draggingConfig, root);
        };

        root.PointerReleased += async (_, e) =>
        {
            if (!ReferenceEquals(_draggingCard, root) || _draggingConfig is null) return;

            e.Pointer.Capture(null);
            root.ZIndex = 0;
            root.Opacity = 1;
            root.Transitions = BuildCardTransitions(); // restore, so its own settle-into-place also animates
            ClearMergeHighlight();

            var draggedConfig = _draggingConfig;
            _draggingCard = null;
            _draggingConfig = null;
            await ResolveDropAsync(draggedConfig, root);
        };
    }

    /// <summary>
    /// Live feedback while dragging, mirroring Windows Start Menu: hovering squarely over a
    /// mergeable tile highlights it (the deliberate "stack these" gesture); anything else previews
    /// the reorder by sliding the affected tiles into their would-be positions in real time, so
    /// letting go there is never a surprise.
    /// </summary>
    private void UpdateDragPreview(TileConfig draggedConfig, Border card)
    {
        var (overlapping, targetRow, targetCol) = FindOverlapping(draggedConfig, card);

        if (overlapping.Count == 1 && IsCenteredOn(card, overlapping[0]) && CanMergeInto(overlapping[0], draggedConfig))
        {
            ShowMergeHighlight(overlapping[0].EntityId);
            ApplyPositions(_currentConfigs, excluding: draggedConfig.EntityId); // no reorder preview while merge-highlighting
            return;
        }

        ClearMergeHighlight();

        int index;
        if (overlapping.Count == 1)
        {
            if (overlapping[0].Size != draggedConfig.Size)
            {
                ApplyPositions(_currentConfigs, excluding: draggedConfig.EntityId); // incompatible sizes — no valid preview, settle back
                return;
            }
            index = ComputeReorderIndex(draggedConfig.EntityId, overlapping[0], card);
        }
        else if (overlapping.Count == 0)
        {
            index = _currentConfigs.Count(c =>
                c.EntityId != draggedConfig.EntityId && (c.Row < targetRow || (c.Row == targetRow && c.Col < targetCol)));
        }
        else
        {
            ApplyPositions(_currentConfigs, excluding: draggedConfig.EntityId); // ambiguous — no valid preview, settle back
            return;
        }

        var preview = SimulateReorder(draggedConfig.EntityId, index);
        ApplyPositions(preview, excluding: draggedConfig.EntityId);
    }

    private static bool CanMergeInto(TileConfig target, TileConfig dragged) =>
        (target.Size == TileSize.Small && dragged.Size == TileSize.Small) ||
        (target.Size == TileSize.Group && dragged.Size == TileSize.Small && (target.GroupEntityIds?.Count ?? 0) < 4);

    private (List<TileConfig> Overlapping, int Row, int Col) FindOverlapping(TileConfig draggedConfig, Border card)
    {
        var colSpan = TileLayoutCompactor.ColSpanFor(draggedConfig.Size);
        var rowSpan = TileLayoutCompactor.RowSpanFor(draggedConfig.Size);
        var row = Math.Max(0, (int)Math.Round((Canvas.GetTop(card) - TileMargin) / CellHeight));
        var col = Math.Clamp((int)Math.Round((Canvas.GetLeft(card) - TileMargin) / CellWidth), 0, TileLayoutCompactor.ColumnCount - colSpan);

        var overlapping = _currentConfigs
            .Where(c => c.EntityId != draggedConfig.EntityId && TileLayoutCompactor.Overlaps(row, col, colSpan, rowSpan, c))
            .ToList();
        return (overlapping, row, col);
    }

    /// <summary>Applies a hypothetical (or the real, settled) arrangement's positions to every currently-rendered card except the one being dragged — animates via each card's own Transitions.</summary>
    private void ApplyPositions(List<TileConfig> arrangement, string excluding)
    {
        foreach (var config in arrangement)
        {
            if (config.EntityId == excluding) continue;
            if (!_cardsByKey.TryGetValue(config.EntityId, out var card)) continue;
            Canvas.SetLeft(card, config.Col * CellWidth + TileMargin);
            Canvas.SetTop(card, config.Row * CellHeight + TileMargin);
        }
    }

    /// <summary>Purely in-memory preview of what Defragment would produce if the dragged tile moved to <paramref name="index"/> — no persistence, just for the live drag animation.</summary>
    private List<TileConfig> SimulateReorder(string draggedEntityId, int index)
    {
        var moved = _currentConfigs.First(c => c.EntityId == draggedEntityId);
        var others = _currentConfigs.Where(c => c.EntityId != draggedEntityId).ToList();
        others.Insert(Math.Clamp(index, 0, others.Count), moved);
        return TileLayoutCompactor.Defragment(others);
    }

    private void ShowMergeHighlight(string key)
    {
        if (_mergeHighlightKey == key) return;
        ClearMergeHighlight();
        if (_cardsByKey.TryGetValue(key, out var card))
        {
            card.BorderBrush = MergeBorderBrush;
            card.BorderThickness = new Thickness(2);
            card.Background = MergeBackgroundBrush;
        }
        _mergeHighlightKey = key;
    }

    private void ClearMergeHighlight()
    {
        if (_mergeHighlightKey is not null && _cardsByKey.TryGetValue(_mergeHighlightKey, out var card))
        {
            card.BorderBrush = NormalBorderBrush;
            card.BorderThickness = new Thickness(1);
            card.Background = NormalBackgroundBrush;
        }
        _mergeHighlightKey = null;
    }

    /// <summary>
    /// Decides what a drop means and commits it. Any overlap with another tile counts as "dropped
    /// near it," but only a drop landing close to that tile's actual center is treated as the
    /// deliberate "stack these together" gesture (merge into/add to a Group) — everything else
    /// (including a drop that overlaps a tile only because there was no literal empty cell between
    /// two neighbors) is treated as plain reordering, so nudging one small tile in between two
    /// others doesn't collapse them into a group by accident.
    /// </summary>
    private async Task ResolveDropAsync(TileConfig draggedConfig, Border card)
    {
        var (overlapping, targetRow, targetCol) = FindOverlapping(draggedConfig, card);

        if (overlapping.Count == 0)
        {
            await MoveToNearestSpotAsync(draggedConfig.EntityId, targetRow, targetCol);
        }
        else if (overlapping.Count == 1)
        {
            var target = overlapping[0];

            if (IsCenteredOn(card, target) && target.Size == TileSize.Small && draggedConfig.Size == TileSize.Small)
                await AppSettings.CreateGroupAsync(target.EntityId, draggedConfig.EntityId);
            else if (IsCenteredOn(card, target) && target.Size == TileSize.Group && draggedConfig.Size == TileSize.Small && (target.GroupEntityIds?.Count ?? 0) < 4)
                await AppSettings.AddToGroupAsync(target.EntityId, draggedConfig.EntityId);
            else if (target.Size == draggedConfig.Size)
                await AppSettings.MoveTileToIndexAsync(draggedConfig.EntityId, ComputeReorderIndex(draggedConfig.EntityId, target, card));
            // else: incompatible sizes and not squarely centered (e.g. a Wide grazing a Small) —
            // reject; RefreshAsync below snaps it back.
        }
        // else: overlaps more than one tile at once — ambiguous, reject; RefreshAsync below snaps it back.

        await RefreshAsync();
    }

    /// <summary>True once the dragged card's actual dropped center is close to the target tile's cell center — the deliberate "stack these" gesture, as opposed to just grazing past it while reordering.</summary>
    private bool IsCenteredOn(Border card, TileConfig target)
    {
        var targetColSpan = TileLayoutCompactor.ColSpanFor(target.Size);
        var targetRowSpan = TileLayoutCompactor.RowSpanFor(target.Size);
        var targetCenterX = target.Col * CellWidth + TileMargin + (targetColSpan * CellWidth - 2 * TileMargin) / 2;
        var targetCenterY = target.Row * CellHeight + TileMargin + (targetRowSpan * CellHeight - 2 * TileMargin) / 2;

        var cardCenterX = Canvas.GetLeft(card) + card.Width / 2;
        var cardCenterY = Canvas.GetTop(card) + card.Height / 2;

        var distance = Math.Sqrt(Math.Pow(cardCenterX - targetCenterX, 2) + Math.Pow(cardCenterY - targetCenterY, 2));
        return distance <= Math.Min(CellWidth, CellHeight) * 0.3;
    }

    /// <summary>Which side of a same-size target tile the drop landed on decides whether the dragged tile ends up just before or just after it in list order.</summary>
    private int ComputeReorderIndex(string entityId, TileConfig target, Border card)
    {
        var targetColSpan = TileLayoutCompactor.ColSpanFor(target.Size);
        var targetCenterX = target.Col * CellWidth + TileMargin + (targetColSpan * CellWidth - 2 * TileMargin) / 2;
        var cardCenterX = Canvas.GetLeft(card) + card.Width / 2;

        var others = _currentConfigs.Where(c => c.EntityId != entityId).ToList();
        var anchorIndex = others.FindIndex(c => c.EntityId == target.EntityId);
        return cardCenterX < targetCenterX ? anchorIndex : anchorIndex + 1;
    }

    /// <summary>Drop landed in genuinely free space — reorders to the nearest reading-order slot rather than a raw Row/Col (which Defragment no longer preserves).</summary>
    private async Task MoveToNearestSpotAsync(string entityId, int targetRow, int targetCol)
    {
        var index = _currentConfigs.Count(c =>
            c.EntityId != entityId && (c.Row < targetRow || (c.Row == targetRow && c.Col < targetCol)));
        await AppSettings.MoveTileToIndexAsync(entityId, index);
    }

    private void AttachQuadrantDragHandlers(Border quadrant, string groupId, string entityId)
    {
        quadrant.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(quadrant).Properties.IsLeftButtonPressed) return;
            e.Handled = true;

            _quadrantDragGroupId = groupId;
            _quadrantDragEntityId = entityId;
            var canvas = this.FindControl<Canvas>("LayoutCanvas")!;
            _quadrantDragStartPointerPos = e.GetPosition(canvas);
            var origin = quadrant.TranslatePoint(new Point(0, 0), canvas) ?? new Point(0, 0);
            _quadrantDragOriginLeft = origin.X;
            _quadrantDragOriginTop = origin.Y;

            e.Pointer.Capture(quadrant);
        };

        quadrant.PointerMoved += (_, e) =>
        {
            if (_quadrantDragEntityId != entityId) return;

            var canvas = this.FindControl<Canvas>("LayoutCanvas")!;
            var pos = e.GetPosition(canvas);
            var delta = pos - _quadrantDragStartPointerPos;

            if (_quadrantGhost is null && (Math.Abs(delta.X) > DragThreshold || Math.Abs(delta.Y) > DragThreshold))
            {
                _quadrantGhost = BuildGhostCard(entityId);
                Canvas.SetLeft(_quadrantGhost, _quadrantDragOriginLeft);
                Canvas.SetTop(_quadrantGhost, _quadrantDragOriginTop);
                _quadrantGhost.ZIndex = 100;
                canvas.Children.Add(_quadrantGhost);
            }

            if (_quadrantGhost is not null)
            {
                Canvas.SetLeft(_quadrantGhost, _quadrantDragOriginLeft + delta.X);
                Canvas.SetTop(_quadrantGhost, _quadrantDragOriginTop + delta.Y);
                UpdateQuadrantDragHighlight(entityId, _quadrantGhost);
            }
        };

        quadrant.PointerReleased += async (_, e) =>
        {
            if (_quadrantDragEntityId != entityId) return;

            e.Pointer.Capture(null);
            var ghost = _quadrantGhost;
            var groupIdCaptured = _quadrantDragGroupId!;
            _quadrantDragGroupId = null;
            _quadrantDragEntityId = null;
            _quadrantGhost = null;
            ClearMergeHighlight();

            if (ghost is null) return; // never moved past the threshold — a plain tap does nothing

            await ExtractFromGroupAndDropAsync(groupIdCaptured, entityId, ghost);
        };
    }

    /// <summary>Same merge-highlight feedback as a normal card drag, for a tile being dragged out of a group — the reorder-preview reflow is skipped here since the entity isn't a real top-level tile yet.</summary>
    private void UpdateQuadrantDragHighlight(string entityId, Border ghost)
    {
        var draggedAsSmall = new TileConfig(entityId);
        var (overlapping, _, _) = FindOverlapping(draggedAsSmall, ghost);

        if (overlapping.Count == 1 && IsCenteredOn(ghost, overlapping[0]) && CanMergeInto(overlapping[0], draggedAsSmall))
            ShowMergeHighlight(overlapping[0].EntityId);
        else
            ClearMergeHighlight();
    }

    private Border BuildGhostCard(string entityId)
    {
        _statesByEntityId.TryGetValue(entityId, out var state);
        var iconKey = state is not null ? HaEntityDisplay.IconFor(state) : "circle";
        var label = state is not null ? HaEntityDisplay.LabelFor(state) : entityId;

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        stack.Children.Add(new PathIcon { Data = Geometry.Parse(TileIcons.PathFor(iconKey)), Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 84, HorizontalAlignment = HorizontalAlignment.Center });

        return new Border
        {
            Width = CellWidth - 2 * TileMargin,
            Height = CellHeight - 2 * TileMargin,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#55808080")),
            BorderBrush = new SolidColorBrush(Colors.Gray),
            BorderThickness = new Thickness(1),
            Opacity = 0.85,
            Child = stack,
        };
    }

    /// <summary>Pops an entity out of its Group first (so it becomes a real top-level Small tile), then resolves where it lands exactly like a normal card drop.</summary>
    private async Task ExtractFromGroupAndDropAsync(string groupId, string entityId, Border ghost)
    {
        await AppSettings.RemoveFromGroupAsync(groupId, entityId);
        await RefreshAsync(); // so _currentConfigs reflects the extraction before resolving the drop target

        var extracted = _currentConfigs.FirstOrDefault(c => c.EntityId == entityId) ?? new TileConfig(entityId);
        await ResolveDropAsync(extracted, ghost);
    }

    private async Task RemoveTileAsync(string entityId)
    {
        var updated = AppSettings.SelectedTiles.Where(t => t.EntityId != entityId).ToList();
        await AppSettings.SetSelectedTilesAsync(updated);
    }
}
