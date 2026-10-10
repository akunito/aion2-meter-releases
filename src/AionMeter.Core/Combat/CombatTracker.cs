using AionMeter.Core.Events;
using AionMeter.Core.Game;

namespace AionMeter.Core.Combat;

/// <summary>
/// Turns the decoded event stream into fight segments. All mutation happens under one lock: the capture thread
/// calls <see cref="Process"/>, the UI thread reads immutable snapshots.
/// </summary>
public sealed class CombatTracker
{
    private readonly object _gate = new();
    private readonly GameData _data;
    private readonly EntityRegistry _entities;
    private readonly List<Encounter> _finished = new(); // newest first
    private readonly List<Encounter> _justFinished = new();
    private Encounter? _current;
    private bool _viewCleared;

    public CombatTracker(GameData data, MeterOptions options)
    {
        _data = data;
        Options = options;
        _entities = new EntityRegistry(data);
    }

    public MeterOptions Options { get; }
    public GameData Data => _data;

    /// <summary>Raised on the processing thread after a segment closes (kill, wipe, idle, zone change, manual).</summary>
    public event Action<FightRecord>? EncounterFinished;

    /// <summary>Raised when the local character is identified.</summary>
    public event Action<string>? SelfIdentified;

    public string? SelfName
    {
        get { lock (_gate) return _entities.Self?.Name; }
    }

    public string? ZoneName
    {
        get { lock (_gate) return _entities.ZoneName; }
    }

    public GameClass SelfClass
    {
        get { lock (_gate) return _entities.Self?.Class ?? GameClass.Unknown; }
    }

    /// <summary>The local character's server (0 until known; restored with the name cache).</summary>
    public int SelfServerId
    {
        get { lock (_gate) return _entities.Self?.ServerId ?? 0; }
    }

    public long LastEventMs { get; private set; }

    // ---------------------------------------------------------------- cache (survives a meter restart)

    /// <summary>Changes whenever something in <see cref="ExportCache"/> does.</summary>
    public int CacheVersion
    {
        get { lock (_gate) return _entities.CacheVersion; }
    }

    /// <summary>Players' names and the bosses around: what a meter restarted in this zone could not learn again.</summary>
    public SessionState ExportCache()
    {
        lock (_gate) return _entities.Export();
    }

    public void ImportCache(SessionState state)
    {
        lock (_gate) _entities.Import(state);
    }

    public void Process(GameEvent e)
    {
        string? selfName = null;
        lock (_gate)
        {
            if (e.TimeMs > LastEventMs) LastEventMs = e.TimeMs;
            CheckIdle(e.TimeMs);

            switch (e)
            {
                case DamageEvent d:
                    OnDamage(d);
                    break;
                case HealEvent h:
                    OnHeal(h);
                    break;
                case SelfIdentifiedEvent s:
                    _entities.SetSelf(s.ActorId, s.Name, s.ServerId, s.Class);
                    _entities.SetGear(s.Name, 0, s.CombatPower);
                    LinkSummonsCastBy(s.ActorId, s.Name);
                    selfName = s.Name;
                    break;
                case PartyRosterEvent roster:
                    foreach (var m in roster.Members) _entities.SetGear(m.Name, m.GearScore, m.CombatPower);
                    _pendingRosters.Add(roster);
                    break;
                case PlayerSeenEvent p:
                    _entities.UpsertPlayer(p.ActorId, p.Name, p.ServerId, p.Class);
                    LinkSummonsCastBy(p.ActorId, p.Name);
                    break;
                case NpcSeenEvent n:
                    if (_entities.IsKnownPlayer(n.ActorId)) break;
                    // The server's own announcement replaces whatever the cache of a previous run said about this id.
                    if (_entities.TryGetNpc(n.ActorId, out var cached) && cached.FromCache) _entities.ForgetNpc(n.ActorId);
                    var npc = _entities.UpsertNpc(n.ActorId, n.NpcCode);
                    if (n.Name is not null) npc.Name = n.Name;
                    _entities.SetMaxHp(npc, n.MaxHp, known: true);
                    if (_current is { IsActive: true } running && running.BossId == n.ActorId) RefreshBoss(running, npc);
                    NoticeBoss(npc, BossNoticeKind.Alive, n.TimeMs, n.X, n.Y, n.Z);
                    break;
                case SummonSeenEvent s:
                    var owner = s.OwnerId != 0 ? s.OwnerId : s.OwnerName is { } on ? _entities.FindPlayerByName(on) ?? 0 : 0;
                    if (owner != 0) LinkSummon(s.ActorId, owner);
                    else _entities.MarkUnowned(s.ActorId, s.TimeMs, s.NpcCode, s.OwnerName);
                    break;
                case CastEvent c:
                    OnCast(c);
                    break;
                case HitNoticeEvent h:
                    OnHitNotice(h);
                    break;
                case FieldBossListEvent list:
                    _pendingLists.Add(list with { ServerId = _entities.Self?.ServerId ?? 0 });
                    break;
                case NpcHpEvent hp:
                    OnNpcHp(hp);
                    break;
                case DeathEvent death:
                    OnDeath(death);
                    break;
                case BattleStateEvent b:
                    OnBattleState(b);
                    break;
                case ZoneChangedEvent z:
                    // A reload of the map we are on keeps every id: an in-map teleport, or the revive after dying on a
                    // boss. The parser knows the map only from the load before; after a meter restart inside a dungeon
                    // the registry knows it from the cache — without that, the first death ended the fight and the boss
                    // came back as "#id".
                    if (z.IsTeleport || z.MapId == _entities.MapId) break;
                    if (_current is { IsActive: true }) Finish(EncounterEndReason.ZoneChange, _current.LastDamageMs);
                    _entities.OnZoneChanged(z.MapId, z.ZoneName);
                    break;
            }
        }
        FlushFinished();
        if (selfName is not null) SelfIdentified?.Invoke(selfName);
    }

    /// <summary>Call periodically with the wall clock so idle segments close even when no packets arrive.</summary>
    public void Tick(long nowMs)
    {
        lock (_gate)
        {
            if (_current is { IsActive: true } enc) AttributeOrphans(enc, nowMs);
            CheckIdle(nowMs);
        }
        FlushFinished();
    }

    /// <summary>Closes the running segment (it is still saved if long enough) and blanks the live view.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_current is { IsActive: true }) Finish(EncounterEndReason.Manual, _current.LastDamageMs);
            _viewCleared = true;
        }
        FlushFinished();
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _finished.Clear();
            _viewCleared = true;
        }
    }

    // ---------------------------------------------------------------- events

    private void OnDamage(DamageEvent d)
    {
        if (d.Damage <= 0 || d.Damage > Options.MaxSingleHit * Math.Max(1, d.HitCount)) return;

        if (_data.IsNonDamageSkill(d.SkillCode)) return; // heals and spirit-link records ride in damage packets too

        var source = _entities.ResolveOwner(d.SourceId);
        var target = d.TargetId;
        // A player's own pet is never an enemy (nor is anyone's spirit / skill effect whose owner is still unknown).
        if (source == target || _entities.IsSummon(target) || _entities.IsUnowned(target)) return;
        if (source != d.SourceId || _entities.IsUnowned(source)) _entities.NoteEntitySkill(d.SkillCode);

        // Class skills mark a player; 7-digit NPC skills mark a mob unless the actor already fought like a player
        // (players fire 7-digit item / godstone procs too).
        var skillClass = GameData.ClassFromSkill(d.SkillCode);
        if (skillClass != GameClass.Unknown)
        {
            _entities.MarkPlayerLike(source);
            if (source == d.SourceId) _entities.ForgetCachedIfClassDiffers(source, skillClass);
        }
        else if (GameData.IsNpcSkill(d.SkillCode) && !_entities.IsPlayerLike(source) && !_entities.IsSummon(d.SourceId))
            _entities.UpsertNpc(source);

        var targetIsPlayer = IsLikelyPlayer(target);
        var sourceIsPlayer = IsLikelyPlayer(source);

        if (targetIsPlayer)
        {
            // Mob → player: incoming damage for the defence view. Player → player (PvP, duels) is ignored for now.
            if (!sourceIsPlayer && _current is { IsActive: true } && _current.Combatants.TryGetValue(target, out var victim))
            {
                victim.DamageTaken += d.Damage;
                victim.HitsTaken++;
            }
            return;
        }
        if (!sourceIsPlayer && IsLikelyNpc(source)) return; // NPC hitting NPC

        var npc = _entities.UpsertNpc(target);
        if (npc.Hp == 0) return; // trailing DoT ticks on a corpse must not open a new segment after the kill

        if (Options.TargetMode == TargetMode.BossOnly)
        {
            if (_current is { IsActive: true, BossId: not null } && !npc.IsBoss) return; // adds during a boss fight
            if (_current is { IsActive: true, BossId: null } && npc.IsBoss && _current.TotalDamage > 0)
                Finish(EncounterEndReason.Idle, _current.LastDamageMs); // trash segment ends when the boss is pulled
        }

        if (_current is not { IsActive: true }) StartEncounter(d.TimeMs);
        var enc = _current!;

        if (npc.IsBoss && (enc.BossId is null || !IsAlive(enc.BossId.Value))) SetBoss(enc, npc, d.TimeMs);

        var c = GetCombatant(enc, source);
        if (c.Class == GameClass.Unknown && (d.Flags & HitFlags.Dot) == 0)
            c.Class = GameData.ClassFromSkill(d.SkillCode);

        var offset = (int)Math.Max(0, d.TimeMs - enc.StartMs);
        var flags = d.HitCount > 1 ? d.Flags | HitFlags.Multi : d.Flags;
        c.AddDamage(offset, d.SkillCode, d.Damage, flags);
        if (d.PowerScalar is >= 1_000 and <= 200_000 && d.SourceId == source) c.Scalars.Add(d.PowerScalar);
        if (c.FirstHitMs == 0) c.FirstHitMs = d.TimeMs;
        c.LastHitMs = d.TimeMs;

        enc.TotalDamage += d.Damage;
        enc.DamageByTarget[target] = enc.DamageByTarget.GetValueOrDefault(target) + d.Damage;
        if (d.TimeMs > enc.LastDamageMs) enc.LastDamageMs = d.TimeMs;
    }

    /// <summary>
    /// Counts who is fighting the boss even when the server sends their damage to nobody but themselves: players by
    /// class skill or announcement, pets under their owner; spirits and effects nobody owns yet are no player.
    /// </summary>
    private void OnHitNotice(HitNoticeEvent h)
    {
        if (_current is not { IsActive: true } enc) return;
        if (h.TargetId != enc.BossId && !enc.DamageByTarget.ContainsKey(h.TargetId)) return;
        if (_entities.IsUnowned(h.ActorId) || _data.IsNonDamageSkill(h.SkillCode)) return;
        var actor = _entities.ResolveOwner(h.ActorId);
        if (actor == h.ActorId && _entities.IsSummon(actor)) return;
        if (_entities.IsKnownPlayer(actor) || (actor == h.ActorId && GameData.ClassFromSkill(h.SkillCode) != GameClass.Unknown))
            enc.Attackers.Add(actor);
    }

    private void OnHeal(HealEvent h)
    {
        if (_current is not { IsActive: true } enc) return;
        var source = _entities.ResolveOwner(h.SourceId);
        if (enc.Combatants.TryGetValue(source, out var c)) c.Healing += h.Amount;
    }

    private void OnNpcHp(NpcHpEvent e)
    {
        if (_entities.IsPlayerLike(e.ActorId)) return;
        // HP/MP updates are sent for players too: only trust them for entities already known to be NPCs.
        if (!e.IsNpc && !_entities.IsKnownNpc(e.ActorId)) return;
        var npc = _entities.UpsertNpc(e.ActorId);
        // A cached boss is only an assumption: an HP pool that does not fit it means the id now belongs to another NPC.
        if (npc.FromCache && npc.MaxHp > 0 && (e.CurrentHp > npc.MaxHp || (e.MaxHp > 0 && e.MaxHp != npc.MaxHp)))
            npc = _entities.ForgetNpc(e.ActorId);
        if (e.CurrentHp <= 0 && npc.Hp > 0)
        {
            NoticeBoss(npc, BossNoticeKind.Killed, e.TimeMs);
            _entities.NoteDeath(npc);
        }
        npc.Hp = e.CurrentHp;
        npc.HpAtMs = e.TimeMs;
        var max = Math.Max(e.MaxHp, Math.Max(npc.MaxHp, e.CurrentHp));
        _entities.SetMaxHp(npc, max, known: npc.MaxHpKnown || e.MaxHp >= max);

        if (_current is not { IsActive: true } enc) return;

        if (enc.BossId is null && npc.IsBoss && enc.DamageByTarget.ContainsKey(e.ActorId)) SetBoss(enc, npc, e.TimeMs);
        if (enc.BossId != e.ActorId) return;

        RefreshBoss(enc, npc);
        if (enc.BossRefHp < 0 && e.CurrentHp > 0) SetBossReference(enc, e.CurrentHp);
        enc.BossHp = e.CurrentHp;
        if (e.CurrentHp < enc.BossLowestHp) enc.BossLowestHp = e.CurrentHp;

        if (e.CurrentHp <= 0)
        {
            Finish(EncounterEndReason.Kill, Math.Max(e.TimeMs, enc.LastDamageMs));
        }
        else if (enc.BossMaxHp > 0 && enc.BossLowestHp < enc.BossMaxHp * 0.9 && e.CurrentHp >= enc.BossMaxHp * 0.995)
        {
            Finish(EncounterEndReason.Wipe, enc.LastDamageMs); // boss healed back to full: the group wiped or reset
        }
    }

    private void OnDeath(DeathEvent e)
    {
        if (!_entities.TryGetNpc(e.ActorId, out var npc)) return;
        if (npc.Hp != 0)
        {
            NoticeBoss(npc, e.AlreadyDead ? BossNoticeKind.SeenDead : BossNoticeKind.Killed, e.TimeMs);
            _entities.NoteDeath(npc);
        }
        npc.Hp = 0;
        npc.HpAtMs = e.TimeMs;
        if (_current is { IsActive: true } enc && enc.BossId == e.ActorId)
        {
            enc.BossHp = 0;
            Finish(EncounterEndReason.Kill, Math.Max(e.TimeMs, enc.LastDamageMs));
        }
    }

    private void OnBattleState(BattleStateEvent e)
    {
        if (_entities.IsPlayerLike(e.ActorId)) return;
        var npc = _entities.UpsertNpc(e.ActorId);
        npc.InCombat = e.InCombat;
        if (e.InCombat || _current is not { IsActive: true } enc || enc.BossId != e.ActorId) return;

        var reason = npc.Hp switch
        {
            0 => EncounterEndReason.Kill,
            > 0 => EncounterEndReason.Wipe,
            _ => EncounterEndReason.Idle,
        };
        Finish(reason, reason == EncounterEndReason.Kill ? Math.Max(e.TimeMs, enc.LastDamageMs) : enc.LastDamageMs);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Pets and skill effects whose owner is unknown deal damage under their own id. Two kinds are handled:
    /// <list type="bullet">
    /// <item>pet-like actors — spawned as a pet / effect, or unannounced and using nothing but pet abilities — go to
    /// the one player whose power scalar they carry, or else to the "unknown summons" row (never a player of their own);</item>
    /// <item>unannounced actors with a tiny skill set (a Cleric's aura, totems) go to the one player of the same class
    /// whose scalars match (exactly, or 1000 lower — a buff state) and who runs a real rotation.</item>
    /// </list>
    /// </summary>
    private void AttributeOrphans(Encounter enc, long nowMs)
    {
        List<(Combatant Orphan, Combatant? Owner)>? moves = null;
        foreach (var o in enc.Combatants.Values)
        {
            if (o.ActorId == Combatant.UnknownSummonsId || _entities.IsKnownPlayer(o.ActorId)) continue;
            var spawned = _entities.TryGetUnowned(o.ActorId, out var entity);
            if (spawned || (o.Skills.Count > 0 && o.Skills.Keys.All(_entities.IsEntitySkill)))
            {
                // Give the name / cast links a moment before settling for the power scalar.
                if (nowMs - (spawned ? entity.SpawnMs : o.FirstHitMs) < 3_000) continue;
                (moves ??= new()).Add((o, ScalarOwner(enc, o, needsRotation: false)));
                continue;
            }
            if (o.Total.Hits + o.Total.DotTicks < 3 || o.Skills.Count > 2) continue;
            if (ScalarOwner(enc, o, needsRotation: true) is { } owner) (moves ??= new()).Add((o, owner));
        }
        if (moves is null) return;
        foreach (var (orphan, owner) in moves)
        {
            (owner ?? UnknownSummons(enc)).MergeFrom(orphan);
            enc.Combatants.Remove(orphan.ActorId);
            if (owner is not null) _entities.SetSummon(orphan.ActorId, owner.ActorId);
        }
    }

    /// <summary>The one player whose power scalars the orphan shares; null when none or several qualify.</summary>
    private Combatant? ScalarOwner(Encounter enc, Combatant o, bool needsRotation)
    {
        if (o.Scalars.Count == 0 || (needsRotation && o.Class == GameClass.Unknown)) return null;
        Combatant? owner = null;
        var candidates = 0;
        foreach (var p in enc.Combatants.Values)
        {
            if (p == o || p.ActorId == Combatant.UnknownSummonsId || p.Class == GameClass.Unknown) continue;
            if (o.Class != GameClass.Unknown && p.Class != o.Class) continue;
            if (_entities.IsUnowned(p.ActorId) || p.Skills.Count < (needsRotation ? 3 * o.Skills.Count : 3)) continue;
            if (!needsRotation && p.Skills.Keys.All(_entities.IsEntitySkill)) continue; // another pet, not an owner
            if (!o.Scalars.Any(s => p.Scalars.Contains(s) || p.Scalars.Contains(s + 1_000))) continue;
            owner = p;
            candidates++;
        }
        return candidates == 1 ? owner : null;
    }

    private static Combatant UnknownSummons(Encounter enc)
    {
        if (enc.Combatants.TryGetValue(Combatant.UnknownSummonsId, out var c)) return c;
        c = new Combatant { ActorId = Combatant.UnknownSummonsId, Name = "Summons" };
        enc.Combatants[c.ActorId] = c;
        return c;
    }

    /// <summary>Ties a pet / effect to its owner; damage it already dealt in the running fight moves along.</summary>
    private void LinkSummon(uint summonId, uint ownerId)
    {
        _entities.SetSummon(summonId, ownerId);
        if (!_entities.IsSummon(summonId)) return; // refused: the id belongs to an announced player
        if (_current is not { IsActive: true } enc || !enc.Combatants.TryGetValue(summonId, out var pet)) return;
        GetCombatant(enc, _entities.ResolveOwner(ownerId)).MergeFrom(pet);
        enc.Combatants.Remove(summonId);
    }

    /// <summary>A character was named: effects spawned with that caster name can now be linked.</summary>
    private void LinkSummonsCastBy(uint playerId, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        foreach (var id in _entities.UnownedCastBy(name)) LinkSummon(id, playerId);
    }

    // Player casts of the last few seconds, to tie lingering skill effects (Bittercold Wind, Fire Wall …) to a caster.
    private readonly List<CastEvent> _recentCasts = new();
    private readonly HashSet<uint> _castLinkTried = new();

    /// <summary>
    /// An effect entity announces its own ticks (actor = target = itself) with its caster's skill variant, last digit
    /// changed (cast 15280140 → ticks 15280141..3). Its owner is the most recent caster of that variant up to 3 s before
    /// it spawned — the variant tells apart players of the same class whose skills are levelled differently.
    /// </summary>
    private void OnCast(CastEvent c)
    {
        if (_entities.TryGetUnowned(c.ActorId, out var entity))
        {
            if (c.TargetId != c.ActorId || !_castLinkTried.Add(c.ActorId)) return;
            var variant = c.SkillCode / 10;
            for (var i = _recentCasts.Count - 1; i >= 0; i--)
            {
                var cast = _recentCasts[i];
                if (cast.TimeMs > entity.SpawnMs + 300) continue;
                if (cast.TimeMs < entity.SpawnMs - 3_000) break;
                if (cast.SkillCode / 10 != variant) continue;
                LinkSummon(c.ActorId, cast.ActorId);
                return;
            }
            return;
        }
        if (_entities.IsSummon(c.ActorId) || _entities.IsKnownNpc(c.ActorId)) return;
        _recentCasts.Add(c);
        var cutoff = c.TimeMs - 6_000;
        var stale = 0;
        while (stale < _recentCasts.Count && _recentCasts[stale].TimeMs < cutoff) stale++;
        if (stale > 0) _recentCasts.RemoveRange(0, stale);
        if (_castLinkTried.Count > 4_096) _castLinkTried.Clear();
    }

    // ---------------------------------------------------------------- boss sightings (respawn timers)

    private readonly List<BossNotice> _pendingNotices = new();
    private readonly List<FieldBossListEvent> _pendingLists = new();
    private readonly List<PartyRosterEvent> _pendingRosters = new();

    /// <summary>A party roster arrived (gear score and combat power of the party). Raised outside the lock.</summary>
    public event Action<PartyRosterEvent>? RosterReceived;

    /// <summary>The in-game map's field boss list arrived (respawn times from the server). Raised outside the lock.</summary>
    public event Action<FieldBossListEvent>? FieldBossListed;

    /// <summary>A world boss (per the data tables) came into view, died, or was found dead. Raised outside the lock.</summary>
    public event Action<BossNotice>? BossNoticed;

    private void NoticeBoss(NpcInfo npc, BossNoticeKind kind, long timeMs, float x = 0, float y = 0, float z = 0)
    {
        if (npc.NpcCode == 0 || !_data.Npcs.TryGetValue(npc.NpcCode, out var def) || !def.IsBoss || def.IsDummy) return;
        if (GameData.IsDungeonMap(_entities.MapId)) return; // instance bosses do not respawn on a timer
        _pendingNotices.Add(new BossNotice(npc.NpcCode, _entities.MapId, _entities.ZoneName, kind, timeMs, x, y, z, _entities.Self?.ServerId ?? 0));
    }

    private bool IsLikelyPlayer(uint id) =>
        _entities.IsPlayerLike(id) || (_current is { IsActive: true } enc && enc.Combatants.ContainsKey(id));

    private bool IsLikelyNpc(uint id) =>
        (_entities.IsKnownNpc(id) && !_entities.IsPlayerLike(id)) ||
        (_current is { IsActive: true } enc && enc.DamageByTarget.ContainsKey(id));

    private bool IsAlive(uint npcId) => !_entities.TryGetNpc(npcId, out var n) || n.Hp != 0;

    private void SetBoss(Encounter enc, NpcInfo npc, long timeMs)
    {
        enc.BossId = npc.ActorId;
        enc.BossHp = npc.Hp;
        enc.BossRefHp = -1;
        // An HP record from a moment ago is where the count starts; an older one may predate damage the meter never saw
        // (others fought the boss meanwhile): then the next HP record is.
        if (npc.Hp > 0 && timeMs - npc.HpAtMs <= BossHpFreshMs) SetBossReference(enc, npc.Hp);
        RefreshBoss(enc, npc);
    }

    private const long BossHpFreshMs = 3_000;

    private static void SetBossReference(Encounter enc, long hp)
    {
        enc.BossRefHp = hp;
        enc.BossRefCounted = enc.BossId is { } id ? enc.DamageByTarget.GetValueOrDefault(id) : 0;
    }

    /// <summary>The boss's HP now: live from the NPC while the fight runs, as it ended otherwise.</summary>
    private long BossHpNow(Encounter enc) =>
        enc.BossId is { } id && enc.IsActive && _entities.TryGetNpc(id, out var npc) ? npc.Hp : enc.BossHp;

    /// <summary>The boss's identity and max HP as known now: the server may announce them after the fight began.</summary>
    private void RefreshBoss(Encounter enc, NpcInfo npc)
    {
        enc.BossCode = npc.NpcCode;
        enc.BossName = _entities.NpcName(npc.ActorId);
        enc.BossMaxHp = npc.MaxHp;
        enc.BossMaxHpKnown = npc.MaxHpKnown;
    }

    private void StartEncounter(long timeMs)
    {
        _current = new Encounter { StartMs = timeMs, LastDamageMs = timeMs, Zone = _entities.ZoneName };
        _viewCleared = false;
    }

    private Combatant GetCombatant(Encounter enc, uint actorId)
    {
        if (enc.Combatants.TryGetValue(actorId, out var c)) return c;
        c = new Combatant { ActorId = actorId };
        RefreshIdentity(c);
        enc.Combatants[actorId] = c;
        return c;
    }

    private void RefreshIdentity(Combatant c)
    {
        if (_entities.TryGetPlayer(c.ActorId, out var p))
        {
            if (!string.IsNullOrEmpty(p.Name)) c.Name = p.Name;
            if (p.Class != GameClass.Unknown) c.Class = p.Class;
            c.ServerId = p.ServerId;
            if (p.GearScore > 0) c.GearScore = p.GearScore;
            if (p.CombatPower > 0) c.CombatPower = p.CombatPower;
        }
        c.IsSelf = _entities.SelfId == c.ActorId;
        if (string.IsNullOrEmpty(c.Name)) c.Name = $"#{c.ActorId}";
    }

    private void CheckIdle(long nowMs)
    {
        if (_current is not { IsActive: true } enc) return;
        var bossAlive = enc.BossId is { } b && IsAlive(b);
        var timeout = bossAlive ? Options.BossIdleTimeoutMs : Options.IdleTimeoutMs;
        if (nowMs - enc.LastDamageMs > timeout) Finish(EncounterEndReason.Idle, enc.LastDamageMs);
    }

    private void Finish(EncounterEndReason reason, long endMs)
    {
        var enc = _current;
        if (enc is null || !enc.IsActive) return;
        enc.Reason = reason;
        enc.EndMs = Math.Max(endMs, enc.StartMs);
        if (enc.TotalDamage <= 0) return;
        AttributeOrphans(enc, long.MaxValue / 2);

        _finished.Insert(0, enc);
        if (_finished.Count > Options.MaxSegments) _finished.RemoveAt(_finished.Count - 1);
        _justFinished.Add(enc);
    }

    private void FlushFinished()
    {
        List<FightRecord>? records = null;
        List<BossNotice>? notices = null;
        List<FieldBossListEvent>? lists = null;
        List<PartyRosterEvent>? rosters = null;
        lock (_gate)
        {
            if (_pendingRosters.Count > 0)
            {
                rosters = new List<PartyRosterEvent>(_pendingRosters);
                _pendingRosters.Clear();
            }
            if (_pendingNotices.Count > 0)
            {
                notices = new List<BossNotice>(_pendingNotices);
                _pendingNotices.Clear();
            }
            if (_pendingLists.Count > 0)
            {
                lists = new List<FieldBossListEvent>(_pendingLists);
                _pendingLists.Clear();
            }
            if (_justFinished.Count > 0)
            {
                records = new List<FightRecord>(_justFinished.Count);
                foreach (var enc in _justFinished)
                {
                    foreach (var c in enc.Combatants.Values) RefreshIdentity(c);
                    records.Add(BuildRecord(enc));
                }
                _justFinished.Clear();
            }
        }
        if (notices is not null && BossNoticed is { } notify)
            foreach (var n in notices) notify(n);
        if (lists is not null && FieldBossListed is { } listed)
            foreach (var l in lists) listed(l);
        if (rosters is not null && RosterReceived is { } received)
            foreach (var r in rosters) received(r);
        if (records is not null && EncounterFinished is { } handler)
            foreach (var r in records) handler(r);
    }

    // ---------------------------------------------------------------- snapshots

    public IReadOnlyList<SegmentInfo> Segments()
    {
        lock (_gate)
        {
            var list = new List<SegmentInfo>(_finished.Count + 1);
            if (_current is { IsActive: true } cur) list.Add(Info(cur));
            foreach (var e in _finished) list.Add(Info(e));
            return list;
        }

        SegmentInfo Info(Encounter e) => new(e.Id, Title(e), ToTime(e.StartMs), e.CombatMs, e.IsActive, e.Reason, e.TotalDamage);
    }

    /// <summary>
    /// <paramref name="segmentId"/> null means "live": the running segment, or the last finished one until a new
    /// fight starts (so results stay readable after a kill).
    /// </summary>
    public EncounterSnapshot? Snapshot(Guid? segmentId, long nowMs)
    {
        lock (_gate)
        {
            var enc = Find(segmentId);
            return enc is null ? null : BuildSnapshot(enc, nowMs);
        }
    }

    public CombatantDetail? Detail(Guid? segmentId, uint actorId)
    {
        lock (_gate)
        {
            var enc = Find(segmentId);
            if (enc is null || !enc.Combatants.TryGetValue(actorId, out var c)) return null;
            RefreshIdentity(c);
            return BuildDetail(enc, c, Combatant.MaxCasts);
        }
    }

    /// <summary>The running fight at a glance: its id, whether a boss is engaged, whether the local player hit anything.</summary>
    public (Guid Id, bool Boss, bool Self)? LiveFight()
    {
        lock (_gate)
        {
            if (_current is not { IsActive: true } e) return null;
            return (e.Id, e.BossId is not null, _entities.SelfId is { } id && e.Combatants.ContainsKey(id));
        }
    }

    public FightRecord? Record(Guid? segmentId)
    {
        lock (_gate)
        {
            var enc = Find(segmentId);
            return enc is null ? null : BuildRecord(enc);
        }
    }

    private Encounter? Find(Guid? id)
    {
        if (id is null)
        {
            if (_current is { IsActive: true }) return _current;
            return _viewCleared ? null : _finished.FirstOrDefault();
        }
        if (_current?.Id == id) return _current;
        return _finished.FirstOrDefault(e => e.Id == id);
    }

    private string Title(Encounter e)
    {
        if (!string.IsNullOrEmpty(e.BossName)) return e.BossName!;
        if (e.DamageByTarget.Count > 0)
        {
            var top = e.DamageByTarget.MaxBy(kv => kv.Value).Key;
            var name = _entities.NpcName(top);
            return e.DamageByTarget.Count > 1 ? $"{name} +{e.DamageByTarget.Count - 1}" : name;
        }
        return e.Zone ?? "Combat";
    }

    /// <summary>NPC template of the fight's main target (boss, else the most damaged NPC) — lets history re-localise titles.</summary>
    private int MainTargetCode(Encounter e)
    {
        if (e.BossCode != 0) return e.BossCode;
        if (e.DamageByTarget.Count == 0) return 0;
        var top = e.DamageByTarget.MaxBy(kv => kv.Value).Key;
        return _entities.TryGetNpc(top, out var npc) ? npc.NpcCode : 0;
    }

    private static DateTimeOffset ToTime(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();

    private EncounterSnapshot BuildSnapshot(Encounter enc, long nowMs)
    {
        var combatMs = enc.CombatMs;
        var seconds = combatMs / 1000.0;
        var total = Math.Max(1, enc.TotalDamage);
        var rows = new List<CombatantSnapshot>(enc.Combatants.Count);
        foreach (var c in enc.Combatants.Values)
        {
            RefreshIdentity(c);
            var t = c.Total;
            rows.Add(new CombatantSnapshot(
                c.ActorId, c.Name, c.Class, c.IsSelf,
                t.Damage, t.Damage / seconds, (double)t.Damage / total,
                t.Hits, t.Hits > 0 ? (double)t.Crits / t.Hits : 0,
                t.Max, c.DamageTaken, c.ServerId, c.GearScore, c.CombatPower));
        }
        rows.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        // Pets without a known owner are not a player: list them last so they never take a place in the ranking.
        if (rows.FindIndex(r => r.ActorId == Combatant.UnknownSummonsId) is var pets and >= 0)
        {
            var row = rows[pets];
            rows.RemoveAt(pets);
            rows.Add(row);
        }

        BossSnapshot? boss = null;
        if (enc.BossId is { } bossId)
        {
            // HP the boss lost before the meter began watching it (beyond the hits it had counted by then): a fight
            // joined late, or a meter (re)started mid-fight.
            var uncounted = enc.BossMaxHpKnown && enc.BossMaxHp > 0 && enc.BossRefHp > 0
                ? Math.Max(0, enc.BossMaxHp - enc.BossRefHp - enc.BossRefCounted)
                : 0;
            boss = new BossSnapshot(bossId, enc.BossCode, enc.BossName ?? _entities.NpcName(bossId), BossHpNow(enc), enc.BossMaxHp,
                enc.BossMaxHpKnown, uncounted);
        }

        return new EncounterSnapshot(
            enc.Id, Title(enc), enc.Zone, ToTime(enc.StartMs), enc.ClockMs(nowMs), combatMs, enc.IsActive, enc.Reason,
            enc.TotalDamage, enc.TotalDamage / seconds, boss, rows, MainTargetCode(enc),
            enc.Attackers.Count + rows.Count(r => !r.IsUnknownSummons && !enc.Attackers.Contains(r.ActorId)));
    }

    private CombatantDetail BuildDetail(Encounter enc, Combatant c, int maxCasts)
    {
        var seconds = enc.CombatMs / 1000.0;
        var total = Math.Max(1, c.Total.Damage);
        var skills = c.Skills.Values
            .Select(s =>
            {
                var st = s.Stats;
                return new SkillRow(
                    s.SkillCode, _data.SkillName(s.SkillCode), st.Hits, st.DotTicks, st.Damage, st.Damage / seconds,
                    (double)st.Damage / total,
                    st.Damage / Math.Max(1, st.Hits + st.DotTicks),
                    st.Hits > 0 ? st.Min : 0, st.Max,
                    Rate(st.Crits, st.Hits), Rate(st.Back, st.Hits), Rate(st.Front, st.Hits),
                    Rate(st.Perfect, st.Hits), Rate(st.Heavy, st.Hits), Rate(st.Multi, st.Hits));
            })
            .OrderByDescending(s => s.Damage)
            .ToList();

        var t = c.Total;
        var quality = new HitQuality(
            t.Hits, t.DotTicks, Rate(t.Crits, t.Hits), Rate(t.Back, t.Hits), Rate(t.Front, t.Hits),
            Rate(t.Perfect, t.Hits), Rate(t.Heavy, t.Hits), Rate(t.Multi, t.Hits),
            t.Max, c.MaxHitSkill != 0 ? _data.SkillName(c.MaxHitSkill) : null);

        var casts = c.Casts.Count <= maxCasts ? c.Casts.ToArray() : c.Casts.GetRange(0, maxCasts).ToArray();
        return new CombatantDetail(
            c.ActorId, c.Name, c.Class, c.IsSelf, t.Damage, t.Damage / seconds, (double)t.Damage / Math.Max(1, enc.TotalDamage),
            enc.CombatMs, c.DamageTaken, c.HitsTaken, quality, skills, c.PerSecond.ToArray(), casts, c.ServerId, c.GearScore, c.CombatPower);
    }

    private FightRecord BuildRecord(Encounter enc)
    {
        var snapshot = BuildSnapshot(enc, enc.EndMs > 0 ? enc.EndMs : enc.LastDamageMs);
        var details = enc.Combatants.Values
            .OrderByDescending(c => c.Total.Damage)
            .Select(c => BuildDetail(enc, c, 5_000))
            .ToList();
        return new FightRecord(1, snapshot, details);
    }

    /// <summary>Null when nothing was measured, so the UI can show a dash instead of a fake zero.</summary>
    private static double? Rate(int count, int hits) => hits > 0 ? (double)count / hits : null;
}
