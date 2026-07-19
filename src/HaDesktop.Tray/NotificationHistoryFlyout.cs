using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace HaDesktop.Tray;

/// <summary>Popup showing the last few notifications received from Home Assistant, opened from the flyout's bell button.</summary>
public static class NotificationHistoryFlyout
{
    public static void Show(Control anchor, IReadOnlyList<NotificationHistoryEntry> entries)
    {
        var content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12), Width = 260 };
        content.Children.Add(new TextBlock { Text = "Recent Notifications", FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold });

        if (entries.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No notifications yet.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var entry in entries.Take(10))
            {
                var entryPanel = new StackPanel { Spacing = 2 };
                if (!string.IsNullOrEmpty(entry.Title))
                    entryPanel.Children.Add(new TextBlock { Text = entry.Title, FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                entryPanel.Children.Add(new TextBlock { Text = entry.Message, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                entryPanel.Children.Add(new TextBlock { Text = entry.ReceivedAt.ToString("g"), FontSize = 10, Opacity = 0.5 });
                content.Children.Add(entryPanel);
            }
        }

        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }
}
