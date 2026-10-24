using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// "Report a problem" on the card's output (A2M-50, port of the Avalonia ReportWindow; WPF Windows/BugReportWindow, A2M-19 + A2M-45): the
/// player's note and the three Privacy ticks (character summary, Top DPS rows, inventory; saved as the next defaults), "Save zip"
/// (reports/report_&lt;ts&gt;.zip, the folder opens) and the GitHub issues page to attach it to.
/// </summary>
internal sealed class ReportWindow : DialogCard
{
    private static UiText T => UiText.Current;
    private readonly Gtk.TextView _note;
    private readonly Gtk.CheckButton _summary, _top, _inventory;
    private readonly Gtk.Label _state;
    private readonly Gtk.Button _create;

    public ReportWindow(AppShell shell, string? prefill = null) : base(shell, "report", UiText.Current.ReportTitle, 560, 520)
    {
        var s = shell.Settings;
        var root = UiKit.VBox();
        UiKit.Margin(root, 12, 0, 12, 12);
        var hint = UiKit.Text(T.ReportHint, 12.5, Fg.Dim);
        hint.MarginBottom = UiKit.Px(10);
        root.Append(hint);
        _note = Gtk.TextView.New();
        _note.WrapMode = Gtk.WrapMode.WordChar;
        _note.AcceptsTab = false;
        _note.GetBuffer().SetText(prefill ?? "", -1);
        var noteScroll = UiKit.Scroll(_note, gutters: false);
        noteScroll.SetSizeRequest(-1, UiKit.Px(120));
        noteScroll.MarginBottom = UiKit.Px(10);
        noteScroll.SetTooltipText(T.ReportDescribe);
        root.Append(noteScroll);
        var always = UiKit.Text(T.ReportAlways, 11.5, Fg.Mute);
        always.MarginBottom = UiKit.Px(2);
        root.Append(always);
        root.Append(_summary = UiKit.Check(T.ShareReportSummary, s.ReportSummary, 2));
        root.Append(_top = UiKit.Check(T.ShareReportTopDps, s.ReportTopDps, 2));
        root.Append(_inventory = UiKit.Check(T.ShareReportInventory, s.ReportInventory, 2));
        root.Append(_state = UiKit.Text("", 11.5, Fg.Dim));
        _state.MarginTop = UiKit.Px(8);
        _state.Visible = false;
        var buttons = UiKit.HBox();
        buttons.Halign = Gtk.Align.End;
        buttons.MarginTop = UiKit.Px(12);
        buttons.Append(UiKit.Ghost(T.UpdateLater, Close));
        buttons.Append(UiKit.Ghost(T.ReportOpenIssue, () => BuildUi.OpenUrl(AppInfo.IssuesUrl)));
        buttons.Append(_create = UiKit.Primary(T.ReportCreate, Create));
        _create.MarginEnd = 0;
        root.Append(buttons);
        SetContent(root);
    }

    protected override void Opened() => _note.GrabFocus();

    private string Note()
    {
        var b = _note.GetBuffer();
        b.GetBounds(out var start, out var end);
        return b.GetText(start, end, false);
    }

    private void State(string text) { _state.Visible = true; _state.SetText(text); }

    /// <summary>The ticks become the next report's defaults (the same switches as Settings → Privacy); settings.json is the UI's to write.</summary>
    private Core.Diagnostics.SharePolicy Policy()
    {
        Shell.RefreshSettings();
        var s = Shell.Settings;
        s.ReportSummary = _summary.Active;
        s.ReportTopDps = _top.Active;
        s.ReportInventory = _inventory.Active;
        Shell.SaveSettings();
        return s.CurrentSharePolicy();
    }

    private void Create()
    {
        _create.Sensitive = false;
        try
        {
            var path = BugReport.Create(Note(), Policy());
            State(string.Format(T.ReportCreated, path));
            Log.Info($"Bug report created: {path}");
            BugReport.Reveal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            State(string.Format(T.ReportFailed, ex.Message));
            Log.Error("Bug report failed", ex);
        }
        _create.Sensitive = true;
    }
}
