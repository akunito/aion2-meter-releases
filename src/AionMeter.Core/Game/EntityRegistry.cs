using AionMeter.Core.Events;

namespace AionMeter.Core.Game;

public sealed class PlayerInfo
{
    public required uint ActorId { get; init; }
    public string Name { get; set; } = "";
    public int ServerId { get; set; }
    public GameClass Class { get; set; }
    /// <summary>Restored from the name cache of a previous run rather than announced in this one.</summary>
    public bool FromCache { get; set; }
    /// <summary>Equipment item level ("GS") from the party roster; 0 = not known.</summary>
    public int GearScore { get; set; }
    /// <summary>Combat power ("CP") from the party roster or the own-character record; 0 = not known.</summary>
    public long CombatPower { get; set; }
}

public sealed record CachedPlayer(uint Id, string Name, GameClass Class, int ServerId, int GearScore = 0, long CombatPower = 0);

/// <summary>A boss as the server announced it: its template (0 = unknown) and max HP (0 = unknown).</summary>
public sealed record CachedNpc(uint Id, int Code, long MaxHp);

/// <summary>What a meter restarted in the same zone needs: who is who, and the bosses around with their real max HP.</summary>
public sealed record SessionState(uint? SelfId, int MapId, IReadOnlyList<CachedPlayer> Players, IReadOnlyList<CachedNpc> Npcs);

/// <summary>A spawned pet / spirit / skill effect whose owner is not known yet.</summary>
public sealed record UnownedEntity(long SpawnMs, int NpcCode, string? OwnerName);

public sealed class NpcInfo
{
    public required uint ActorId { get; init; }
    public int NpcCode { get; set; }
    public long MaxHp { get; set; }
    /// <summary>
    /// The max HP came from the server (the spawn record, or an HP record carrying it). False while it is only the
    /// highest HP seen so far: the meter started after the NPC came into view, so the real max may be higher.
    /// </summary>
    public bool MaxHpKnown { get; set; }
    /// <summary>-1 until an HP record arrives; 0 = dead.</summary>
    public long Hp { get; set; } = -1;
    /// <summary>When <see cref="Hp"/> last came from the server (capture time, ms).</summary>
    public long HpAtMs { get; set; }
    public bool InCombat { get; set; }
    public bool IsBoss { get; set; }
    public bool IsDummy { get; set; }
    public string? Name { get; set; }
    /// <summary>Restored from the cache of a previous run rather than announced in this one.</summary>
    public bool FromCache { get; set; }
}

/// <summary>
/// Maps actor ids seen on the wire to players, NPCs and summons. Entity ids are reissued on every map load, so
/// everything but the local player is dropped on zone change. The server announces a player's name only when they
/// come into view or on a loading screen, and an NPC's template and max HP only when it comes into view, so both are
/// also exported to a cache that survives a meter restart.
/// </summary>
public sealed class EntityRegistry
{
    private readonly Dictionary<uint, PlayerInfo> _players = new();
    private readonly Dictionary<uint, NpcInfo> _npcs = new();
    private readonly Dictionary<uint, uint> _summonOwner = new();
    private readonly Dictionary<uint, UnownedEntity> _unowned = new();
    private readonly HashSet<int> _entitySkills = new();
    private readonly HashSet<uint> _classSkillUsers = new();
    // Gear score and combat power by character name: the roster names its members, the world knows them by entity id.
    // Kept across zones (names do not change on a loading screen).
    private readonly Dictionary<string, (int Gear, long Power)> _gearByName = new();
    private readonly GameData _data;

    public EntityRegistry(GameData data) => _data = data;

    /// <summary>NPCs at or above this max HP count as bosses when the data files do not know them.</summary>
    public long BossHpThreshold { get; set; } = 5_000_000;

    public uint? SelfId { get; private set; }
    public PlayerInfo? Self => SelfId is { } id && _players.TryGetValue(id, out var p) ? p : null;
    public string? ZoneName { get; private set; }
    public int MapId { get; private set; }

    /// <summary>Bumped whenever a player's identity or a boss's changes, so the cache knows when to save.</summary>
    public int CacheVersion { get; private set; }

    public void SetSelf(uint actorId, string name, int serverId, GameClass cls)
    {
        if (SelfId is { } old && old != actorId)
        {
            if (_players.TryGetValue(old, out var prev) && prev.Name == name) _players.Remove(old);
            // Our own id changed while the cache was in use: the zone was reloaded behind our back, so every cached
            // id now belongs to someone else.
            DropCached();
        }
        SelfId = actorId;
        UpsertPlayer(actorId, name, serverId, cls);
    }

    public PlayerInfo UpsertPlayer(uint actorId, string name, int serverId, GameClass cls)
    {
        if (!_players.TryGetValue(actorId, out var p))
        {
            p = new PlayerInfo { ActorId = actorId };
            _players[actorId] = p;
        }
        p.FromCache = false;
        CacheVersion++;
        if (!string.IsNullOrWhiteSpace(name))
        {
            // A name belongs to one entity at a time: forget stale ids still carrying it.
            if (p.Name != name)
                foreach (var other in _players.Values.Where(o => o.ActorId != actorId && o.Name == name).ToList())
                    _players.Remove(other.ActorId);
            p.Name = name;
            ApplyGear(p);
        }
        if (serverId != 0) p.ServerId = serverId;
        if (cls != GameClass.Unknown) p.Class = cls;
        _npcs.Remove(actorId);
        _summonOwner.Remove(actorId);
        _unowned.Remove(actorId);
        return p;
    }

    public NpcInfo UpsertNpc(uint actorId, int npcCode = 0)
    {
        if (!_npcs.TryGetValue(actorId, out var n))
        {
            n = new NpcInfo { ActorId = actorId };
            _npcs[actorId] = n;
        }
        if (npcCode != 0 && n.NpcCode != npcCode)
        {
            n.NpcCode = npcCode;
            if (_data.Npcs.TryGetValue(npcCode, out var def))
            {
                n.IsBoss = def.IsBoss || def.IsDummy;
                n.IsDummy = def.IsDummy;
            }
            if (n.IsBoss) CacheVersion++;
        }
        return n;
    }

    /// <param name="known">The value came from the server rather than being the highest current HP seen so far.</param>
    public void SetMaxHp(NpcInfo npc, long maxHp, bool known)
    {
        if (maxHp <= 0) return;
        var changed = npc.MaxHp != maxHp || (known && !npc.MaxHpKnown);
        npc.MaxHp = maxHp;
        if (known) npc.MaxHpKnown = true;
        // Without a data entry, a big HP pool is the best boss signal we have.
        var listed = npc.NpcCode != 0 && _data.Npcs.ContainsKey(npc.NpcCode);
        if (!listed && maxHp >= BossHpThreshold) npc.IsBoss = true;
        if (changed && npc.IsBoss) CacheVersion++;
    }

    /// <summary>
    /// Starts over on an NPC id: a fresh announcement replaces what the cache said about it, or the cache turned out to
    /// describe another entity (ids are reissued on every map load).
    /// </summary>
    public NpcInfo ForgetNpc(uint actorId)
    {
        if (_npcs.Remove(actorId, out var old) && old.IsBoss) CacheVersion++;
        return UpsertNpc(actorId);
    }

    /// <summary>A boss died: it leaves the cache.</summary>
    public void NoteDeath(NpcInfo npc)
    {
        if (npc.IsBoss) CacheVersion++;
    }

    public void SetSummon(uint summonId, uint ownerId)
    {
        if (summonId == ownerId) return;
        if (_players.TryGetValue(summonId, out var p))
        {
            if (!p.FromCache) return;
            _players.Remove(summonId); // a cached id now reused by someone's pet
            CacheVersion++;
        }
        _summonOwner[summonId] = ownerId;
        _npcs.Remove(summonId);
        _unowned.Remove(summonId);
        _classSkillUsers.Remove(summonId);
    }

    /// <summary>
    /// A pet / spirit / skill effect spawned without a resolvable owner. It stays out of the player list whatever skills
    /// it uses, until an owner is found (by the caster's name, a cast it can be tied to, or its power scalar).
    /// </summary>
    public void MarkUnowned(uint actorId, long timeMs, int npcCode, string? ownerName)
    {
        if (_players.TryGetValue(actorId, out var p))
        {
            if (!p.FromCache) return;
            _players.Remove(actorId);
            CacheVersion++;
        }
        _npcs.Remove(actorId);
        _summonOwner.Remove(actorId);
        _classSkillUsers.Remove(actorId);
        _unowned[actorId] = new UnownedEntity(timeMs, npcCode, ownerName);
    }

    public bool IsUnowned(uint actorId) => _unowned.ContainsKey(actorId);
    public bool TryGetUnowned(uint actorId, out UnownedEntity entity) => _unowned.TryGetValue(actorId, out entity!);

    /// <summary>Unowned entities that carry this character name as their caster.</summary>
    public List<uint> UnownedCastBy(string name) =>
        _unowned.Where(kv => kv.Value.OwnerName == name).Select(kv => kv.Key).ToList();

    /// <summary>Skills seen from pets and skill effects: an unannounced actor using only these is not a player.</summary>
    public void NoteEntitySkill(int skillCode) => _entitySkills.Add(skillCode);

    public bool IsEntitySkill(int skillCode) => skillCode < 1_000_000 || _entitySkills.Contains(skillCode);

    /// <summary>
    /// A character's gear score and combat power (0 = leave as it is), by name: from the party roster or the own record.
    /// Every player with that name gets them, now and whenever they are announced later.
    /// </summary>
    public void SetGear(string name, int gearScore, long combatPower)
    {
        if (string.IsNullOrEmpty(name) || (gearScore <= 0 && combatPower <= 0)) return;
        var (gear, power) = _gearByName.GetValueOrDefault(name);
        if (gearScore > 0) gear = gearScore;
        if (combatPower > 0) power = combatPower;
        if (_gearByName.TryGetValue(name, out var known) && known == (gear, power)) return;
        _gearByName[name] = (gear, power);
        foreach (var p in _players.Values)
            if (p.Name == name) ApplyGear(p);
        CacheVersion++;
    }

    private void ApplyGear(PlayerInfo p)
    {
        if (!_gearByName.TryGetValue(p.Name, out var g)) return;
        if (g.Gear > 0) p.GearScore = g.Gear;
        if (g.Power > 0) p.CombatPower = g.Power;
    }

    public uint? FindPlayerByName(string name)
    {
        foreach (var p in _players.Values)
            if (p.Name == name) return p.ActorId;
        return null;
    }

    /// <summary>
    /// The entity cast a class skill: it is a player (or fights like one), whatever weaker hints said. Players whose
    /// identity record we missed would otherwise be mistaken for mobs when they use a 7-digit item / godstone skill.
    /// </summary>
    public void MarkPlayerLike(uint actorId)
    {
        if (_unowned.ContainsKey(actorId) || !_classSkillUsers.Add(actorId)) return;
        if (_npcs.TryGetValue(actorId, out var n) && n.NpcCode == 0 && n.MaxHp == 0) _npcs.Remove(actorId);
    }

    public bool IsPlayerLike(uint actorId) => _players.ContainsKey(actorId) || _classSkillUsers.Contains(actorId);

    public bool TryGetPlayer(uint actorId, out PlayerInfo player) => _players.TryGetValue(actorId, out player!);
    public bool TryGetNpc(uint actorId, out NpcInfo npc) => _npcs.TryGetValue(actorId, out npc!);
    public bool IsKnownPlayer(uint actorId) => _players.ContainsKey(actorId);
    public bool IsKnownNpc(uint actorId) => _npcs.ContainsKey(actorId);
    public bool IsSummon(uint actorId) => _summonOwner.ContainsKey(actorId);

    /// <summary>Follows summon → owner links (pets, spirits, totems) so their damage is credited to the player.</summary>
    public uint ResolveOwner(uint actorId)
    {
        for (var i = 0; i < 4 && _summonOwner.TryGetValue(actorId, out var owner); i++) actorId = owner;
        return actorId;
    }

    public string NpcName(uint actorId)
    {
        if (_npcs.TryGetValue(actorId, out var n))
        {
            if (!string.IsNullOrEmpty(n.Name)) return n.Name;
            if (n.NpcCode != 0) return _data.NpcName(n.NpcCode);
        }
        return $"#{actorId}";
    }

    public void OnZoneChanged(int mapId, string zoneName)
    {
        MapId = mapId;
        ZoneName = zoneName;
        _npcs.Clear();
        _summonOwner.Clear();
        _unowned.Clear();
        _classSkillUsers.Clear();
        // Ids are reissued: other players' names would now point at strangers. Ours is re-sent right after the load.
        var self = Self;
        _players.Clear();
        if (self is not null) _players[self.ActorId] = self;
        CacheVersion++;
    }

    // ------------------------------------------------------------------ cache (survives a meter restart)

    public SessionState Export() => new(
        SelfId, MapId,
        _players.Values.Where(p => p.Name.Length > 0)
            .Select(p => new CachedPlayer(p.ActorId, p.Name, p.Class, p.ServerId, p.GearScore, p.CombatPower)).ToList(),
        // Living bosses the server described; a max HP only guessed from current HP is no better than nothing.
        _npcs.Values.Where(n => n.IsBoss && n.Hp != 0 && (n.NpcCode != 0 || n.MaxHpKnown))
            .Take(64)
            .Select(n => new CachedNpc(n.ActorId, n.NpcCode, n.MaxHpKnown ? n.MaxHp : 0))
            .ToList());

    /// <summary>Restores what a previous run in the same zone knew. Live announcements always win.</summary>
    public void Import(SessionState state)
    {
        MapId = state.MapId;
        if (state.MapId != 0) ZoneName ??= _data.MapName(state.MapId);
        foreach (var c in state.Players)
        {
            if (c.GearScore > 0 || c.CombatPower > 0) _gearByName.TryAdd(c.Name, (c.GearScore, c.CombatPower));
            if (_players.ContainsKey(c.Id)) continue;
            _players[c.Id] = new PlayerInfo
            {
                ActorId = c.Id, Name = c.Name, Class = c.Class, ServerId = c.ServerId, FromCache = true,
                GearScore = c.GearScore, CombatPower = c.CombatPower,
            };
        }
        foreach (var c in state.Npcs)
        {
            if (_players.ContainsKey(c.Id) || _npcs.ContainsKey(c.Id)) continue;
            var npc = UpsertNpc(c.Id, c.Code);
            SetMaxHp(npc, c.MaxHp, known: true);
            npc.FromCache = true;
        }
        if (state.SelfId is { } s && _players.ContainsKey(s)) SelfId = s;
    }

    /// <summary>A cached name whose entity now fights as another class belongs to someone else: forget it.</summary>
    public void ForgetCachedIfClassDiffers(uint actorId, GameClass observed)
    {
        if (observed == GameClass.Unknown || !_players.TryGetValue(actorId, out var p) || !p.FromCache) return;
        if (p.Class != GameClass.Unknown && p.Class != observed)
        {
            _players.Remove(actorId);
            if (SelfId == actorId) SelfId = null;
            CacheVersion++;
        }
    }

    private void DropCached()
    {
        foreach (var id in _players.Values.Where(p => p.FromCache).Select(p => p.ActorId).ToList()) _players.Remove(id);
        foreach (var id in _npcs.Values.Where(n => n.FromCache).Select(n => n.ActorId).ToList()) _npcs.Remove(id);
        CacheVersion++;
    }

    public void Clear()
    {
        _players.Clear();
        _npcs.Clear();
        _summonOwner.Clear();
        _unowned.Clear();
        _gearByName.Clear();
        SelfId = null;
    }
}
