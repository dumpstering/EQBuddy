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
/// announced (<see cref="AppSettings.ManualTeammates"/>). A hand-added name is kept for the
/// watched character only, and is a JOIN, not a standing member: it counts from the start
/// of the session it was added in until the log shows them leave, the group end or a log
/// out (<see cref="ManualTeammate"/>). A change re-derives the current session through
/// <see cref="LogWatcher.RederiveTeammatesAsync"/>, so a name added mid-session counts
/// from the session's start.
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
        "Your teammates come from your own log, with no file from them: a group join, an "
        + "accepted invite, group chat or three of their kills that earn you party XP adds "
        + "them; leaving, a disband or logging out ends it. Their damage, kills, heals and "
        + "damage taken join your totals, here and on your phone; their XP, loot and coin "
        + "stay yours. Keep the chat filters for other players' hits and misses on. A name "
        + "you add counts for this character from this session's start until your log "
        + "shows them leave, the group end or you log out.";

    private readonly MainWindow _main;
    private readonly Func<object, object> _resource;
    private TextBlock _detected = null!;
    private StackPanel _names = null!;
    private TextBox _input = null!;
    private TextBlock _refusal = null!;
    private DispatcherTimer? _repaint;
    private string _rowsDrawn = "";

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
        // Live while on screen: a join or leave line the log writes now shows up here
        // without the player reopening Options.
        panel.Loaded += (_, _) =>
        {
            _repaint ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                (_, _) => { PaintDetected(); BuildNameRows(); }, panel.Dispatcher);
            _repaint.Start();
            PaintDetected();
        };
        panel.Unloaded += (_, _) => _repaint?.Stop();
        return panel;
    }

    private void PaintDetected() =>
        _detected.Text = TeammatesPresentation.DetectedLine(_main._watcher.Teammates.CountedNow());

    /// <summary>The watched character's log, whose hand-added names this row shows and edits.</summary>
    private CharacterLog? Watched => _main._watcher.CurrentPath is { } p ? CharacterLog.FromPath(p) : null;

    /// <summary>Rebuilt only when a name or whether it is counted moved, so the 2 s repaint
    /// never pulls a ✕ out from under the pointer for nothing.</summary>
    private void BuildNameRows()
    {
        // A hand-edited settings.json can carry "ManualTeammates": null.
        var who = Watched;
        var saved = ManualTeammates.NamesFor(_main.Settings.ManualTeammates ??= [], who?.Character, who?.Server);
        var counted = _main._watcher.Teammates.CountedNow();
        var rows = saved.Select(n => (Name: n, Counted: counted.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
        var drawn = string.Join("|", rows.Select(r => $"{r.Name}:{r.Counted}"));
        if (_names.Children.Count > 0 && drawn == _rowsDrawn) return;
        _rowsDrawn = drawn;

        _names.Children.Clear();
        _names.Children.Add(Dim(TeammatesPresentation.ManualHeading(rows.Count)));
        foreach (var (name, isCounted) in rows)
        {
            var row = new Grid { Margin = new Thickness(12, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = TeammatesPresentation.ManualRow(name, isCounted), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            row.Children.Add(label);
            var remove = new Button
            {
                Style = (Style)_resource("IconButton"), Content = "✕", FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0), ToolTip = $"Take {name} off the names you added",
            };
            remove.Click += (_, _) =>
            {
                ManualTeammates.Remove(_main.Settings.ManualTeammates ??= [], name, who?.Character, who?.Server);
                Apply();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _names.Children.Add(row);
        }
    }

    private void Add()
    {
        var who = Watched;
        string? refusal;
        if (who is null) refusal = TeammatesPresentation.NoLogRefusal;
        else if (TeammatesPresentation.TryNormalizeName(_input.Text, who.Character,
                     _main._watcher.Teammates.CountedNow(), out var name, out refusal))
        {
            var since = _main._watcher.Teammates.JoinTimeForHandAdded(name, DateTime.Now);
            (_main.Settings.ManualTeammates ??= []).Add(new ManualTeammate(name, who.Character, who.Server, since));
            _input.Text = "";
            Apply();
            return;
        }
        _refusal.Text = refusal ?? "";
        _refusal.Visibility = Visibility.Visible;
    }

    /// <summary>Save, hand the new list to the watcher, and re-derive the current session
    /// from it.</summary>
    private void Apply()
    {
        _refusal.Visibility = Visibility.Collapsed;
        _main.Settings.Save();
        _main._watcher.Teammates.Manual = _main.Settings.ManualTeammates;
        _ = _main._watcher.RederiveTeammatesAsync();
        _rowsDrawn = "\0";   // the list changed: redraw now
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
