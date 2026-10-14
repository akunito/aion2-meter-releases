using System.Text;

namespace AionMeter.Core.Capture;

/// <summary>Everything the meter offers, split by whether it needs the live capture (Npcap) or runs on files, web data and the user's own input.</summary>
public enum CaptureFeature
{
    // live only: they decode the game's traffic
    Dps,
    GameTimers,
    LiveState,
    AtlasPosition,
    PacketRecording,
    // no capture needed
    Atlas,
    Prices,
    Craft,
    TopDps,
    BuildPlanner,
    ItemsFromFiles,
    SharedBossTimers,
    AbyssLogs,
    FightHistory,
}

/// <summary>
/// A2M-27: whether the capture library (Npcap on Windows) is on this PC, and what that costs. Every place that would touch SharpPcap asks
/// <see cref="Present"/> first: without wpcap.dll SharpPcap's first call throws DllNotFoundException / TypeInitializationException, and
/// the type initialiser failure then repeats for the life of the process. The answer is re-probed at most every <see cref="ProbeEveryMs"/>
/// so an install during the session is picked up without a restart, and UI ticks can ask it freely (two File.Exists per probe).
/// </summary>
public sealed class CaptureAvailability
{
    // A2M-48: on Linux "Npcap missing" sent the reader to the wrong library (NixOS 2026-10-13: libpcap is a split package and
    // ${libpcap}/lib held only pkgconfig, the .so lives in the lib output). The Windows strings stay byte-identical: UiText translations key on them.
    public static readonly string NoCaptureStatus = NoCaptureStatusFor(OperatingSystem.IsWindows());
    public static readonly string NpcapNotInstalled = NotInstalledFor(OperatingSystem.IsWindows());

    public static string NoCaptureStatusFor(bool windows) =>
        windows ? "No capture (Npcap missing)" : "No capture (libpcap missing: install libpcap / set LD_LIBRARY_PATH)";

    public static string NotInstalledFor(bool windows) => windows ? "Npcap not installed" : "libpcap not loadable";

    public static readonly CaptureFeature[] All = Enum.GetValues<CaptureFeature>();
    public static readonly CaptureFeature[] LiveOnly =
        [CaptureFeature.Dps, CaptureFeature.GameTimers, CaptureFeature.LiveState, CaptureFeature.AtlasPosition, CaptureFeature.PacketRecording];

    public static bool IsLiveOnly(CaptureFeature f) => f <= CaptureFeature.PacketRecording;

    public static readonly IReadOnlyDictionary<CaptureFeature, string> EnglishLabels = new Dictionary<CaptureFeature, string>
    {
        [CaptureFeature.Dps] = "DPS meter and fight breakdowns",
        [CaptureFeature.GameTimers] = "timers read from the in-game event list",
        [CaptureFeature.LiveState] = "live inventory and character state",
        [CaptureFeature.AtlasPosition] = "your position on the Atlas",
        [CaptureFeature.PacketRecording] = "packet recording (.pcap)",
        [CaptureFeature.Atlas] = "Atlas (maps, nodes, events)",
        [CaptureFeature.Prices] = "Prices",
        [CaptureFeature.Craft] = "Craft",
        [CaptureFeature.TopDps] = "Top DPS rankings",
        [CaptureFeature.BuildPlanner] = "Build and Daevanion planner",
        [CaptureFeature.ItemsFromFiles] = "Items from the last saved files",
        [CaptureFeature.SharedBossTimers] = "Boss Timers from the shared feed",
        [CaptureFeature.AbyssLogs] = "Abyss Logs lines of saved fights",
        [CaptureFeature.FightHistory] = "fight history",
    };

    private readonly Func<bool> _probe;
    private long _probedAt;
    private volatile bool _probed;
    private volatile bool _present;

    public CaptureAvailability(Func<bool> probe, int probeEveryMs = 10_000)
    {
        _probe = probe;
        ProbeEveryMs = probeEveryMs;
    }

    /// <summary>Npcap's wpcap.dll on Windows (its own folder, or System32 in WinPcap-compatible mode); elsewhere libpcap is assumed (tests, CLI).</summary>
    public static CaptureAvailability OfThisPc() => new(DefaultProbe);

    public static bool DefaultProbe() => !OperatingSystem.IsWindows() || IsNpcapInstalled();

    public static bool IsNpcapInstalled() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll")) ||
        File.Exists(Path.Combine(Environment.SystemDirectory, "wpcap.dll"));

    public int ProbeEveryMs { get; }

    public bool Present
    {
        get
        {
            var now = Environment.TickCount64;
            if (!_probed || now - Interlocked.Read(ref _probedAt) >= ProbeEveryMs)
            {
                _present = _probe();
                Interlocked.Exchange(ref _probedAt, now);
                _probed = true;
            }
            return _present;
        }
    }

    /// <summary>A2M-27: "host:port" while the capture source is another meter's forwarded stream (<see cref="PcapStreamSource"/>), else null.
    /// That stream is read without the capture library, so the live features work on a PC (a VM, Wine) that has no Npcap.</summary>
    public string? ForwardedFrom { get; set; }

    /// <summary>The live features can work: the capture library is here, or the packets come forwarded from another meter.</summary>
    public bool Live => ForwardedFrom is not null || Present;

    /// <summary>Packet recording and .pcap replay still go through SharpPcap, so they need the library even when the capture is forwarded.</summary>
    public bool Has(CaptureFeature f) => !IsLiveOnly(f) || (f == CaptureFeature.PacketRecording ? Present : Live);

    /// <summary>The Settings → Capture paragraph; empty while the capture works.</summary>
    public string SettingsText(IReadOnlyDictionary<CaptureFeature, string> labels, string headline, string lostHeader, string keptHeader)
    {
        if (Live) return "";
        var sb = new StringBuilder(512);
        sb.Append(headline).Append('\n').Append(lostHeader).Append(' ');
        Join(sb, labels, true);
        sb.Append('\n').Append(keptHeader).Append(' ');
        Join(sb, labels, false);
        return sb.ToString();
    }

    private static void Join(StringBuilder sb, IReadOnlyDictionary<CaptureFeature, string> labels, bool liveOnly)
    {
        var first = true;
        foreach (var f in All)
        {
            if (IsLiveOnly(f) != liveOnly) continue;
            if (!first) sb.Append(", ");
            sb.Append(labels.TryGetValue(f, out var l) ? l : EnglishLabels[f]);
            first = false;
        }
        sb.Append('.');
    }

    /// <summary>The capture-health line (boss log, Settings): adapter counters, or why there are none.</summary>
    public string HealthText(CaptureHealth? health) =>
        ForwardedFrom is { } from ? (health is { } f ? $"forwarded capture from {from}: {f.Received:N0} packets, {f.Gaps:N0} stream gaps" : "forwarded capture from " + from)
        : !Present ? NpcapNotInstalled
        : health is { } h ? $"{h.Received:N0} packets, {h.Dropped:N0} dropped, {h.Gaps:N0} stream gaps"
        : "no adapter open";
}
