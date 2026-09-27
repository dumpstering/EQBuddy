using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy;

/// <summary>
/// **Options → Behavior → Teammates.** Teammates are worked out from the player's OWN log —
/// there is no file to pick — so this row has two jobs: show, live, who the log has put in
/// the group, and let the player add or remove names by hand for someone the log never
/// announced (<see cref="AppSettings.TeammateNames"/>). A change re-derives the current
/// session through <see cref="LogWatcher.RederiveTeammatesAsync"/>, so a name added
/// mid-session counts from the session's start.
///
/// Its own class, like <see cref="SettingsTelemetryView"/>, so the upstream Behavior block
/// gains one line; each <see cref="SettingsBehaviorView"/> builds its own instance (trap 45),
/// and both hosts share the one <see cref="AppSettings"/> (trap 13). The explanation hangs on
/// the heading's ⓘ, the same "hung on a HEADING" shape as the hotkeys: the name rows below
/// it are rebuilt on every add or remove (<c>SettingsProsePass2Tests</c> holds the row).
/// </summary>
internal sealed class SettingsTeammatesView
{
    /// <summary>What the feature does and does not see, and the one setting it depends on.</summary>
    private const string TeammatesBlurb =
        "Your teammates come from your own log: a group join, an invite you accept or group "
        + "chat adds them, and what your log shows of them (damage, kills, heals, damage "
        + "taken) joins your totals here and on your phone. No file from them is needed. "
        + "Their XP, loot and coin never appear in your log, so those stay yours. Keep the "
        + "chat filters for other players' hits and misses switched on, or their fighting "
        + "never reaches your log. Add a name for someone who was grouped before your log began.";

    private readonly MainWindow _main;
    private readonly Func<object, object> _resource;
    private TextBlock _detected = null!;
    private StackPanel _names = null!;
    private TextBox _input = null!;
    private TextBlock _refusal = null!;
    private DispatcherTimer? _repaint;

    public SettingsTeammatesView(MainWindow main, Func<object, object> resource)
    {
        _main = main;
        _resource = resource;
    }

    private UIElement? _block;
    public UIElement Block => _block ??= Build();

    private UIElement Build()
    {
        var panel = new StackPanel();
        var heading = new TextBlock
        {
            Text = "Teammates", FontSize = 12, FontWeight = FontWeights.SemiBold,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        panel.Children.Add(DesignSystem.HintRow(heading, Hint(TeammatesBlurb), new Thickness(0, 14, 0, 4)));

        _detected = Dim("");
        panel.Children.Add(_detected);

        _names = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(_names);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _input = new TextBox { Width = 140, FontSize = 12, ToolTip = TeammatesPresentation.AddPlaceholderTip };
        _input.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        _input.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _input.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
        addRow.Children.Add(_input);
        var add = new Button
        {
            Content = "Add teammate", Style = (Style)_resource("ActionButton"),
            Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3),
        };
        add.Click += (_, _) => Add();
        addRow.Children.Add(add);
        panel.Children.Add(addRow);

        _refusal = Dim("");
        _refusal.Visibility = Visibility.Collapsed;
        panel.Children.Add(_refusal);

        BuildNameRows();
        PaintDetected();
        // Live while on screen: a join line the log writes now shows up here without the
        // player reopening Options.
        panel.Loaded += (_, _) =>
        {
            _repaint ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                (_, _) => PaintDetected(), panel.Dispatcher);
            _repaint.Start();
            PaintDetected();
        };
        panel.Unloaded += (_, _) => _repaint?.Stop();
        return panel;
    }

    private void PaintDetected() =>
        _detected.Text = TeammatesPresentation.DetectedLine(_main._watcher.Teammates.AutoDetected);

    private void BuildNameRows()
    {
        _names.Children.Clear();
        var saved = _main.Settings.TeammateNames;
        _names.Children.Add(Dim(TeammatesPresentation.ManualHeading(saved.Count)));
        foreach (var name in saved.ToList())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 2, 0, 0) };
            var label = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            row.Children.Add(label);
            var remove = new Button
            {
                Style = (Style)_resource("IconButton"), Content = "✕", FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0), ToolTip = $"Stop counting {name}",
            };
            remove.Click += (_, _) =>
            {
                _main.Settings.TeammateNames.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                Apply();
            };
            row.Children.Add(remove);
            _names.Children.Add(row);
        }
    }

    private void Add()
    {
        if (!TeammatesPresentation.TryNormalizeName(_input.Text, _main._watcher.CurrentPath is { } p
                ? CharacterLog.FromPath(p)?.Character : null,
                _main.Settings.TeammateNames, out var name, out var refusal))
        {
            _refusal.Text = refusal ?? "";
            _refusal.Visibility = Visibility.Visible;
            return;
        }
        _main.Settings.TeammateNames.Add(name);
        _input.Text = "";
        Apply();
    }

    /// <summary>Save, hand the new list to the watcher, and re-derive the current session
    /// from it.</summary>
    private void Apply()
    {
        _refusal.Visibility = Visibility.Collapsed;
        _main.Settings.Save();
        _main._watcher.Teammates.ManualNames = _main.Settings.TeammateNames;
        _ = _main._watcher.RederiveTeammatesAsync();
        BuildNameRows();
    }

    private Button Hint(string prose)
    {
        var hint = DesignSystem.InfoHint(prose);
        hint.Margin = new Thickness(DesignTokens.SpaceXs, 0, 0, 0);
        return hint;
    }

    private TextBlock Dim(string text) => new()
    {
        Text = text, Style = (Style)_resource("Dim"),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
    };
}
