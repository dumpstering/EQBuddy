using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy;

internal static class TeammateStatusView
{
    public static void Refresh(TextBlock warning, Ellipse status, Ellipse mini, SessionStats stats)
    {
        var paired = stats.Companion is not null;
        var offset = stats.EstimatedTeammateClockOffset;
        var message = paired ? TeammateStatusPresentation.Warning(offset) : null;
        warning.Text = message ?? "";
        warning.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (!paired) return;
        var detail = message ?? TeammateStatusPresentation.ClockStatus(offset);
        status.ToolTip = $"{status.ToolTip}\n{detail}";
        mini.ToolTip = $"{mini.ToolTip}\n{detail}";
        if (message is null) return;
        status.Fill = mini.Fill = (Brush)warning.FindResource("WarnBrush");
    }
}
