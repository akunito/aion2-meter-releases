using System.Text.Json;

namespace AionMeter.Core.Builds;

/// <summary>Grade: Common, Rare, Legend, Unique or None (start). Type: Stat, SkillLevel or Start. Effects*[i] are the same effect in both languages.</summary>
public sealed record DaevNode(int Id, int Board, int Row, int Col, string Grade, string Type, string NameEn, string NameEs, string[] EffectsEn, string[] EffectsEs,
                              string Sprite, int Cost, bool Start)
{
    public string Name(string lang) => lang == "es" && NameEs.Length > 0 ? NameEs : NameEn;
    public string[] Effects(string lang) => lang == "es" && EffectsEs.Length == EffectsEn.Length ? EffectsEs : EffectsEn;
    public bool IsSkill => Type == "SkillLevel";
}

/// <summary>One board of a class. Total excludes the start node; Nodes are in catalogue order; Adj = 4-neighbours by node id.</summary>
public sealed class DaevBoard
{
    public int Id { get; init; }
    public string NameEn { get; init; } = "";
    public string NameEs { get; init; } = "";
    public int Total { get; init; }
    public int Order { get; init; }
    public int Start { get; init; }
    public DaevNode[] Nodes { get; init; } = [];
    public Dictionary<int, int[]> Adj { get; init; } = new();
    public int MinRow { get; init; } = 1;
    public int MaxRow { get; init; } = 15;
    public int MinCol { get; init; } = 1;
    public int MaxCol { get; init; } = 15;

    public string Name(string lang) => lang == "es" && NameEs.Length > 0 ? NameEs : NameEn;
    public int[] Neighbours(int nodeId) => Adj.TryGetValue(nodeId, out var n) ? n : [];
}

/// <summary>The five boards of one class plus every node by id (ids are unique across a class's boards).</summary>
public sealed class DaevClass
{
    public static readonly DaevClass Empty = new("", [], new());

    public string ClassId { get; }
    public DaevBoard[] Boards { get; }
    private readonly Dictionary<int, DaevNode> _nodes;

    public DaevClass(string classId, DaevBoard[] boards, Dictionary<int, DaevNode> nodes) { ClassId = classId; Boards = boards; _nodes = nodes; }

    public DaevNode? Node(int id) => _nodes.GetValueOrDefault(id);

    public DaevBoard? Board(int id)
    {
        foreach (var b in Boards) if (b.Id == id) return b;
        return null;
    }
}

/// <summary>
/// Daevanion boards per class from data/gamedb/daevanion/&lt;class&gt;.json (tools/daevanion-catalog.py, official API), loaded on first
/// use of the class. Node id = board*10000 + (row-1)*15 + col. Missing or broken files give an empty class, never an exception.
/// </summary>
public sealed class DaevanionBook
{
    public const string SpriteCdn = "https://cdn.questlog.gg/aion-2/assets/Game/UI/Resource/Sprite/FWindow_Daevanion/";

    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, DaevClass> _byClass = new(StringComparer.OrdinalIgnoreCase);

    public DaevanionBook(string directory) { _dir = directory; }

    /// <summary>The CDN has no disabled look for the start sprite.</summary>
    public static string SpriteUrl(string stem, bool disabled) => SpriteCdn + stem + (disabled ? "_Disabled_Sprite.webp" : "_Sprite.webp");

    public DaevClass ForClass(string? classId)
    {
        if (string.IsNullOrEmpty(classId)) return DaevClass.Empty;
        lock (_lock)
        {
            if (_byClass.TryGetValue(classId, out var c)) return c;
            c = Load(classId);
            _byClass[classId] = c;
            return c;
        }
    }

    private DaevClass Load(string classId)
    {
        var path = Path.Combine(_dir, classId.ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return DaevClass.Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var nodes = new Dictionary<int, DaevNode>(600);
            var perBoard = new Dictionary<int, List<DaevNode>>();
            foreach (var n in root.GetProperty("nodes").EnumerateArray())
            {
                var (nameEn, nameEs) = Pair(n, "name");
                string[] effEn = [], effEs = [];
                if (n.TryGetProperty("effects", out var e) && e.ValueKind == JsonValueKind.Object) { effEn = Strings(e, "en"); effEs = Strings(e, "es"); }
                var node = new DaevNode(n.GetProperty("id").GetInt32(), Int(n, "board"), Int(n, "row"), Int(n, "col"), Str(n, "grade") ?? "Common", Str(n, "type") ?? "Stat",
                                        nameEn, nameEs, effEn, effEs, Str(n, "sprite") ?? "", Int(n, "cost"), n.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.True);
                nodes[node.Id] = node;
                if (!perBoard.TryGetValue(node.Board, out var list)) perBoard[node.Board] = list = new List<DaevNode>(160);
                list.Add(node);
            }
            var boards = new List<DaevBoard>(5);
            foreach (var b in root.GetProperty("boards").EnumerateArray())
            {
                var id = b.GetProperty("id").GetInt32();
                var bn = perBoard.TryGetValue(id, out var l) ? l.ToArray() : [];
                var adj = new Dictionary<int, int[]>(bn.Length);
                if (b.TryGetProperty("adj", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var p in a.EnumerateObject())
                    {
                        if (!int.TryParse(p.Name, out var k) || p.Value.ValueKind != JsonValueKind.Array) continue;
                        var arr = new int[p.Value.GetArrayLength()]; var i = 0;
                        foreach (var x in p.Value.EnumerateArray()) arr[i++] = x.GetInt32();
                        adj[k] = arr;
                    }
                int r0 = 15, r1 = 1, c0 = 15, c1 = 1;
                foreach (var n in bn) { r0 = Math.Min(r0, n.Row); r1 = Math.Max(r1, n.Row); c0 = Math.Min(c0, n.Col); c1 = Math.Max(c1, n.Col); }
                if (bn.Length == 0) { r0 = c0 = 1; r1 = c1 = 15; }
                var (en, es) = Pair(b, "name");
                boards.Add(new DaevBoard
                {
                    Id = id, NameEn = en, NameEs = es, Total = Int(b, "total"), Order = Int(b, "order"), Start = Int(b, "start"), Nodes = bn, Adj = adj,
                    MinRow = r0, MaxRow = r1, MinCol = c0, MaxCol = c1,
                });
            }
            boards.Sort((x, y) => x.Order.CompareTo(y.Order));
            return new DaevClass(classId.ToLowerInvariant(), boards.ToArray(), nodes);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return DaevClass.Empty; }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string[] Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return [];
        var r = new string[a.GetArrayLength()]; var i = 0;
        foreach (var x in a.EnumerateArray()) r[i++] = x.GetString() ?? "";
        return r;
    }

    private static (string En, string Es) Pair(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object) return ("", "");
        return (Str(o, "en") ?? "", Str(o, "es") ?? "");
    }
}
