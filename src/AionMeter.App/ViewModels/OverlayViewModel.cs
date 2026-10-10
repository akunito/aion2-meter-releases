using System.Collections.ObjectModel;
using System.Windows.Media;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;

namespace AionMeter.App.ViewModels;

public sealed class RowViewModel : ObservableObject
{
    private uint _actorId;
    private string _name = "";
    private GameClass _class;
    private bool _isSelf;
    private string _dps = "";
    private string _damage = "";
    private string _share = "";
    private double _fill;
    private int _rank;
    private string _tooltip = "";
    private string _gearScore = "";
    private string _combatPower = "";

    public uint ActorId { get => _actorId; set => Set(ref _actorId, value); }
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>"1859": gear score from the party roster; empty when not known.</summary>
    public string GearScore
    {
        get => _gearScore;
        set
        {
            if (Set(ref _gearScore, value)) Raise(nameof(HasGearScore));
        }
    }

    public bool HasGearScore => _gearScore.Length > 0;

    /// <summary>"82.1K": combat power from the party roster or the own record; empty when not known.</summary>
    public string CombatPower
    {
        get => _combatPower;
        set
        {
            if (Set(ref _combatPower, value)) Raise(nameof(HasCombatPower));
        }
    }

    public bool HasCombatPower => _combatPower.Length > 0;
    public string Dps { get => _dps; set => Set(ref _dps, value); }
    public string Damage { get => _damage; set => Set(ref _damage, value); }
    public string Share { get => _share; set => Set(ref _share, value); }
    /// <summary>0..1 width of the coloured bar.</summary>
    public double Fill { get => _fill; set => Set(ref _fill, value); }
    public string Tooltip { get => _tooltip; set => Set(ref _tooltip, value); }

    public bool IsSelf
    {
        get => _isSelf;
        set
        {
            if (Set(ref _isSelf, value)) UpdateNameBrush();
        }
    }

    /// <summary>Place in the ranking (1 = top damage). Top three get gold / silver / bronze badges.</summary>
    public int Rank
    {
        get => _rank;
        set
        {
            if (!Set(ref _rank, value)) return;
            var (bg, fg, border) = RankBrushes.For(value);
            RankBackground = bg;
            RankForeground = fg;
            RankBorder = border;
            Raise(nameof(RankText));
            Raise(nameof(RankBackground));
            Raise(nameof(RankForeground));
            Raise(nameof(RankBorder));
        }
    }

    /// <summary>The badge text; rows without a place (pets of unknown owners) show a dot.</summary>
    public string RankText => _rank > 0 ? _rank.ToString() : "·";

    public Brush RankBackground { get; private set; } = RankBrushes.For(99).Bg;
    public Brush RankForeground { get; private set; } = RankBrushes.For(99).Fg;
    public Brush RankBorder { get; private set; } = RankBrushes.For(99).Border;

    /// <summary>Thin progress bar: muted class colour on the left, full colour at the tip.</summary>
    public Brush BarBrush { get; private set; } = Brushes.Transparent;
    /// <summary>Full-height row fill: saturated class colour that eases off towards the end of the bar.</summary>
    public Brush FillBrush { get; private set; } = Brushes.Transparent;
    /// <summary>DPS figure: a light tint of the class colour, readable on top of the fill.</summary>
    public Brush DpsBrush { get; private set; } = Brushes.White;
    /// <summary>Class-coloured name (yours is white and underlined).</summary>
    public Brush NameBrush { get; private set; } = Brushes.White;
    /// <summary>Icon tile frame and its glow.</summary>
    public Brush TileBorder { get; private set; } = Brushes.Transparent;
    public Color GlowColor { get; private set; } = Colors.Transparent;

    public GameClass Class
    {
        get => _class;
        set
        {
            if (!Set(ref _class, value) && BarBrush != Brushes.Transparent) return;
            var c = ClassVisuals.ColorOf(value);
            var dark = Mix(c, Color.FromRgb(0x10, 0x14, 0x20), 0.55);
            BarBrush = Freeze(new LinearGradientBrush(dark, Lighten(c, 0.12), 0));
            TileBorder = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, c.R, c.G, c.B)));
            GlowColor = c;

            var v = ClassVisuals.BarColorOf(value);
            var fill = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            fill.GradientStops.Add(new GradientStop(Lighten(v, 0.08), 0));
            fill.GradientStops.Add(new GradientStop(v, 0.55));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, (byte)(v.R * 0.72), (byte)(v.G * 0.72), (byte)(v.B * 0.72)), 1));
            FillBrush = Freeze(fill);
            DpsBrush = Freeze(new SolidColorBrush(Lighten(v, 0.55)));
            Raise(nameof(FillBrush));
            Raise(nameof(DpsBrush));
            Raise(nameof(BarBrush));
            Raise(nameof(TileBorder));
            Raise(nameof(GlowColor));
            UpdateNameBrush();
        }
    }

    private void UpdateNameBrush()
    {
        NameBrush = IsSelf ? Brushes.White : Freeze(new SolidColorBrush(Lighten(ClassVisuals.ColorOf(Class), 0.18)));
        Raise(nameof(NameBrush));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

public static class RankBrushes
{
    private static readonly (Brush Bg, Brush Fg, Brush Border)[] Medals =
    [
        (Gradient(0xF6, 0xDB, 0x8E, 0xC9, 0x9A, 0x45), Solid(0x1A, 0x14, 0x0A), Solid(0xFF, 0xEB, 0xB0)), // gold
        (Gradient(0xEE, 0xF2, 0xF7, 0xA7, 0xB2, 0xC1), Solid(0x12, 0x16, 0x1F), Solid(0xFF, 0xFF, 0xFF)), // silver
        (Gradient(0xE9, 0xA8, 0x7A, 0xA8, 0x63, 0x3B), Solid(0x1A, 0x0F, 0x08), Solid(0xF5, 0xC3, 0x9E)), // bronze
    ];

    private static readonly (Brush Bg, Brush Fg, Brush Border) Plain =
        (Solid(0x1A, 0x20, 0x30), Solid(0xF3, 0xE6, 0xC8), Solid(0x6B, 0x75, 0x90));

    public static (Brush Bg, Brush Fg, Brush Border) For(int rank) => rank is >= 1 and <= 3 ? Medals[rank - 1] : Plain;

    private static Brush Solid(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 90);
        brush.Freeze();
        return brush;
    }
}

public sealed class OverlayViewModel : ObservableObject
{
    private static readonly Brush LiveRed = Frozen(Color.FromRgb(0xEF, 0x4D, 0x56));
    private static readonly Brush SavedGold = Frozen(Color.FromRgb(0xC9, 0xA4, 0x5C));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x93, 0xA8));

    private string _title = UiText.Current.WaitingTitle;
    private string _clock = "0:00";
    private bool _isActive;
    private bool _hasData;
    private bool _hasBoss;
    private string _bossHpText = "";
    private string _bossPercent = "";
    private double _bossHpFraction = 1;
    private string _partyDps = "—";
    private string _totalDamage = "—";
    private string _status = "Starting…";
    private Brush _statusBrush = Brushes.Gray;
    private string _modeLabel = "BOSS";
    private string _detail = "";
    private string _topHit = "";
    private string _topHitBy = "";
    private string _footer = "";
    private string _headerLabel = "";
    private Brush _headerBrush = Dim;
    private string _selfPlace = "";
    private Brush _selfPlaceBrush = RankBrushes.For(99).Bg;
    private Brush _selfPlaceForeground = RankBrushes.For(99).Fg;

    public ObservableCollection<RowViewModel> Rows { get; } = new();

    /// <summary>Overlay wording in the chosen language (column headers, labels).</summary>
    public UiText T => UiText.Current;

    /// <summary>"EN" / "RU" on the toolbar switch.</summary>
    public string LanguageCode => UiText.Current.Code.ToUpperInvariant();

    public void LanguageChanged()
    {
        Raise(nameof(T));
        Raise(nameof(LanguageCode));
    }

    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>The footer's timer button: "Timers", or the next respawn ("Gartua 12:34").</summary>
    public string TimersLabel { get => _timersLabel; set => Set(ref _timersLabel, value); }
    /// <summary>One pill per recurring server event ("Shugo 39m", "Rift ● 42m" while running), updated in place every second.</summary>
    public System.Collections.ObjectModel.ObservableCollection<EventPill> EventLabels { get; } = new();
    private string _timersLabel = UiText.Current.TimersButton;
    public string Clock { get => _clock; set => Set(ref _clock, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    public bool HasBoss { get => _hasBoss; set => Set(ref _hasBoss, value); }
    /// <summary>"135,382,562 HP"</summary>
    public string BossHpText { get => _bossHpText; set => Set(ref _bossHpText, value); }
    /// <summary>"58.9%"</summary>
    public string BossPercent { get => _bossPercent; set => Set(ref _bossPercent, value); }
    public double BossHpFraction { get => _bossHpFraction; set => Set(ref _bossHpFraction, value); }
    public string PartyDps { get => _partyDps; set => Set(ref _partyDps, value); }
    public string TotalDamage { get => _totalDamage; set => Set(ref _totalDamage, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public string ModeLabel { get => _modeLabel; set => Set(ref _modeLabel, value); }
    /// <summary>"Krao Cave · Kill"</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    /// <summary>Biggest single hit of the fight, "1,052,914".</summary>
    public string TopHit { get => _topHit; set => Set(ref _topHit, value); }
    public string TopHitBy { get => _topHitBy; set => Set(ref _topHitBy, value); }
    /// <summary>Tooltip of the title: the biggest hit of the fight and how to step through fights.</summary>
    public string TitleTip { get => _titleTip; set => Set(ref _titleTip, value); }
    private string _titleTip = "";
    /// <summary>"5 players · party 4.0M/s" (the fight's length is the clock in the header).</summary>
    public string Footer { get => _footer; set => Set(ref _footer, value); }

    /// <summary>The shown fight is one where the server kept most players' damage from us (see
    /// <see cref="EncounterSnapshot.MostDamageUnsent"/>).</summary>
    public bool MostDamageUnsent { get; private set; }
    /// <summary>Letter-spaced card label: "● LIVE FIGHT", "SAVED · 05.10 19:04" …</summary>
    public string HeaderLabel { get => _headerLabel; private set => Set(ref _headerLabel, value); }
    public Brush HeaderBrush { get => _headerBrush; private set => Set(ref _headerBrush, value); }

    /// <summary>"3 / 19" — the local player's place in the current ranking; empty when we are not in it.</summary>
    public string SelfPlace { get => _selfPlace; set => Set(ref _selfPlace, value); }
    public Brush SelfPlaceBrush { get => _selfPlaceBrush; set => Set(ref _selfPlaceBrush, value); }
    public Brush SelfPlaceForeground { get => _selfPlaceForeground; set => Set(ref _selfPlaceForeground, value); }

    public Guid? SegmentId { get; private set; }
    public string Version { get; } = "v" + (typeof(OverlayViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    /// <summary>Gear score and combat power columns (Settings → Overlay).</summary>
    public bool ShowGear { get; set; } = true;

    /// <summary>False while the overlay is too narrow for them next to the names (the fight's numbers come first).</summary>
    public bool GearFits { get; set; } = true;

    /// <summary>Someone in the ranking has a gear score or combat power: the GS / CP column labels show.</summary>
    public bool HasGear { get => _hasGear; private set => Set(ref _hasGear, value); }
    private bool _hasGear;

    public void Apply(EncounterSnapshot? snap, int maxRows, bool relativeToTop)
    {
        if (snap is null)
        {
            SegmentId = null;
            HasData = false;
            HasBoss = false;
            IsActive = false;
            Title = T.WaitingTitle;
            Detail = T.WaitingDetail;
            TitleTip = T.TipScrollFights;
            Clock = "0:00";
            PartyDps = "—";
            TotalDamage = "—";
            TopHit = "";
            Footer = "";
            MostDamageUnsent = false;
            SelfPlace = "";
            HasGear = false;
            Rows.Clear();
            return;
        }

        SegmentId = snap.Id;
        HasData = true;
        IsActive = snap.IsActive;
        Title = T.FightTitle(snap);
        // Running: wall clock since the pull. Finished: the fight's real length (first to last hit).
        Clock = Format.ClockShort(snap.IsActive ? snap.ClockMs : snap.CombatMs);
        PartyDps = Format.Compact(snap.PartyDps) + "/s";
        TotalDamage = Format.Compact(snap.TotalDamage);

        var result = snap.Reason switch
        {
            EncounterEndReason.Kill => T.Kill,
            EncounterEndReason.Wipe => T.Wipe,
            EncounterEndReason.None => T.InProgress,
            _ => T.Ended,
        };
        var zone = snap.Zone == "Open world" ? T.OpenWorld : snap.Zone;
        // Part of the boss's HP went before the meter could count it (started or restarted mid-fight): say how much,
        // in place of the zone, so the numbers are not taken for the whole fight.
        var partial = snap.Boss switch
        {
            { MaxHpKnown: false, Hp: not 0 } => T.HpUnknown,
            { CountedFrom: { } from } => string.Format(T.CountedFrom, Format.Percent(from, 0)),
            _ => null,
        };
        Detail = partial is not null ? $"{result} · {partial}" : string.IsNullOrEmpty(zone) ? result : $"{zone} · {result}";
        // A world boss whose crowd the server kept from us: the party's DPS would be a fraction of the real one.
        MostDamageUnsent = snap.MostDamageUnsent;
        Footer = MostDamageUnsent ? T.DamageUnsentLine(snap) : $"{T.Players(snap.PlayerCount)} · {T.Party} {PartyDps}";

        var top = snap.Combatants.MaxBy(x => x.MaxHit);
        TopHit = top is { MaxHit: > 0 } ? Format.Grouped(top.MaxHit) : "";
        TopHitBy = top is { MaxHit: > 0 } ? string.Format(T.TopHitBy, top.Name) : "";
        // The top line holds only the title: the biggest hit lives in its tooltip.
        TitleTip = (TopHit.Length > 0 ? $"{TopHitBy}: {TopHit}\n" : "") + T.TipScrollFights;

        if (snap.Boss is { } boss && boss.MaxHp > 0 && boss.Hp >= 0)
        {
            HasBoss = true;
            BossHpFraction = boss.HpFraction;
            // "counted from 82% HP" rides in the bar now that there is no detail line.
            BossHpText = boss.Hp == 0 ? T.Defeated : Format.Grouped(boss.Hp) + " HP" + (partial is null ? "" : " · " + partial);
            BossPercent = boss.MaxHpKnown || boss.Hp == 0 ? Format.Percent(boss.HpFraction) : "?";
        }
        else
        {
            HasBoss = false;
        }

        var list = snap.Combatants;
        var count = Math.Min(maxRows, list.Count);
        // Always keep the local player visible even when they are outside the top N.
        var selfIndex = -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].IsSelf) { selfIndex = i; break; }
        var showSelfExtra = selfIndex >= count;
        var rowsNeeded = count + (showSelfExtra ? 1 : 0);

        while (Rows.Count > rowsNeeded) Rows.RemoveAt(Rows.Count - 1);
        while (Rows.Count < rowsNeeded) Rows.Add(new RowViewModel());

        var best = list.Count > 0 ? Math.Max(1, list[0].Damage) : 1;
        for (var r = 0; r < rowsNeeded; r++)
        {
            var i = showSelfExtra && r == rowsNeeded - 1 ? selfIndex : r;
            var cb = list[i];
            var row = Rows[r];
            row.ActorId = cb.ActorId;
            row.Rank = cb.IsUnknownSummons ? 0 : i + 1;
            row.Name = T.CombatantLabel(cb.ActorId, cb.Name, cb.Class);
            row.GearScore = ShowGear && GearFits && cb.GearScore > 0 ? cb.GearScore.ToString() : "";
            row.CombatPower = ShowGear && GearFits && cb.CombatPower > 0 ? Format.Power(cb.CombatPower) : "";
            row.Class = cb.Class;
            row.IsSelf = cb.IsSelf;
            row.Dps = Format.Compact(cb.Dps) + "/s";
            row.Damage = Format.Compact(cb.Damage);
            row.Share = Format.Share(cb.Share);
            row.Fill = relativeToTop ? (double)cb.Damage / best : cb.Share;
            var heading = cb.IsUnknownSummons ? T.UnknownSummonsTip : $"#{i + 1} {cb.Name} · {T.ClassName(cb.Class)}";
            row.Tooltip = $"{heading}\n" +
                          $"{T.RowDamage} {Format.Grouped(cb.Damage)} ({Format.Share(cb.Share)}) · DPS {Format.Compact(cb.Dps)}\n" +
                          $"{T.RowHits} {cb.Hits} · {T.RowCrit} {Format.Percent(cb.CritRate)} · {T.RowMax} {Format.Grouped(cb.MaxHit)}";
        }
        HasGear = Rows.Any(r => r.HasGearScore || r.HasCombatPower);
        SelfPlace = selfIndex >= 0 ? $"{selfIndex + 1} / {snap.PlayerCount}" : "";
        SelfPlaceBrush = RankBrushes.For(selfIndex + 1).Bg;
        SelfPlaceForeground = RankBrushes.For(selfIndex + 1).Fg;
    }

    /// <summary>Card label above the title. <paramref name="kind"/>: live / last / segment / saved.</summary>
    public void SetSegment(string kind, string? when = null)
    {
        (HeaderLabel, HeaderBrush) = kind switch
        {
            "live" when !HasData => (Spaced(T.WaitingLabel), Dim),
            "live" when IsActive => (Spaced(T.LiveFight), LiveRed),
            "live" => (Spaced(T.LastFight), Dim),
            "saved" => (Spaced(T.SavedFight) + "   " + when, SavedGold),
            _ => (Spaced(T.ThisSession) + "   " + when, SavedGold),
        };
    }

    /// <summary>WPF has no letter-spacing: thin spaces between letters give the card label its airy caps.</summary>
    private static string Spaced(string text) => string.Join(' ', text.ToUpperInvariant().ToCharArray());

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
