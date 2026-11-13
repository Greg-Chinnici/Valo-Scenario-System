// VrpReplay.cs - standalone reader/writer for .vrp (compact Valorant replay) files.
// Drop this file, ReplayTags.cs, AgentCatalog.cs and MapCatalog.cs into a Unity project (Assets/...). No other dependencies.
// Coordinates are in VrpReplay.Space: Unity (metres, Y-up, Z forward / X right; vrfconv default) or Unreal
// (centimetres, Z-up, X forward / Y right). Use replay.UnityPosition / UnityRotation / UnityPoint / UnityDirection,
// which work for either; ConvertToUnitySpace() converts an Unreal-space file after loading.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace ValorantReplay.Playback
{
    public enum TeamRole : byte { Unknown = 0, Attacking = 1, Defending = 2 }

    /// <summary>Coordinate space of all positions/directions/angles in a VrpReplay.
    /// Unreal: cm, Z-up, X forward, Y right; Pitch = up positive.
    /// Unity: m, Y-up, Z forward, X right (x = ueY/100, y = ueZ/100, z = ueX/100); Yaw = Unity Y euler, Pitch = Unity X euler (down positive).</summary>
    public enum CoordinateSpace : byte { Unreal = 0, Unity = 1 }

    [Flags]
    public enum FrameFlags : byte
    {
        None = 0,
        Alive = 1,        // player is alive at this tick
        HasData = 2,      // a real movement sample exists near this tick (otherwise position is held)
        OffMap = 4,       // position is an off-map staging location (e.g. ultimate arenas)
        Snapped = 8,      // teleport between this tick and the previous one (do not interpolate)
        Crouching = 16,   // crouch held
        Walking = 32,     // walk key held, i.e. silent movement
        Airborne = 64,    // jumping or falling
        Blinded = 128,    // an active blind (flash) on this player
    }

    /// <summary>Body region of a hit (Valorant RegionalDamage).</summary>
    public enum HitRegion : byte { Body = 0, Head = 1, Legs = 2, None = 5 }

    /// <summary>Round phase (GameState MulticastSetPhase). SwitchSides is the halftime transition.</summary>
    public enum RoundPhase : byte { Unknown = 0, Setup = 1, Reset = 2, Buy = 3, Combat = 4, RoundEnd = 5, SwitchSides = 6 }

    /// <summary>Spike state (GameState BombState). Detonated is inferred; it was not seen in the test replays.</summary>
    public enum SpikeState : byte { None = 0, AtSpawn = 1, Dropped = 2, Carried = 3, Planted = 4, Detonated = 5, Defused = 6 }

    /// <summary>Why a round ended. Derived from spike events, alive state, phases and the winner; Unknown if unclear.
    /// Surrender = decided without the round reaching combat (forfeit vote during buy phase).</summary>
    public enum RoundEndReason : byte { Unknown = 0, Elimination = 1, Defused = 2, Detonated = 3, TimeExpired = 4, Surrender = 5 }

    /// <summary>UltimateUsed/UltimateEnded come from PlayerState.bUltimateActive (one pair per activation).</summary>
    public enum MatchEventType : byte { UltimateUsed = 1, SpikeDefused = 2, SideSwitch = 3, UltimateEnded = 4, SpikeDetonated = 5 }

    /// <summary>Blind: flashes (BlindManager). The rest are debuffs from the effect system; Reveal = the player's
    /// position was exposed (Fade Haunt/Prowler trail, Cypher trapwire/ult, Sova dart).</summary>
    public enum StatusEffectType : byte
    {
        Blind = 1, Nearsight = 2, Concuss = 3, Suppress = 4, Slow = 5, Detain = 6, Netted = 7, Vulnerable = 8, Reveal = 9
    }

    /// <summary>A timed effect on a player. Source is an index into Entities (-1 = unknown or the caster directly).</summary>
    public sealed class StatusEffect
    {
        public float Time, Duration;           // 0 = a one-off ping with no end (reveal pings: Cypher ult, Sova dart) - show briefly
        public byte Player = 255, SourcePlayer = 255;
        public StatusEffectType Type;
        public int Source = -1;
        public string Name = "";               // effect asset, e.g. "FXC_Vampire_4_NearsightAOE_Nearsight" ("" for blinds)
        public bool ActiveAt(float time) => time >= Time && time < Time + Duration;
        /// <summary>A reveal ping or other effect without a duration.</summary>
        public bool IsPing => Duration <= 0f;
    }

    /// <summary>Who holds the spike from Time on. Holder: player index, <see cref="VrpReplay.HolderWingman"/>
    /// (Gekko's Wingman) or <see cref="VrpReplay.HolderNone"/> (on the ground, in the air, planted).</summary>
    public struct SpikeCarrierSample { public float Time; public byte Holder; }

    /// <summary>One defuse attempt. Progress is in seconds of the 7 s defuse; an attempt starts at 0 or at the
    /// 3.5 s half checkpoint and progresses linearly.</summary>
    public sealed class DefuseAttempt
    {
        public float StartTime, EndTime;
        public byte Player = 255;
        public float ProgressStart, ProgressEnd;
        public bool Defused;
        public float ProgressAt(float time) =>
            time <= StartTime ? ProgressStart : time >= EndTime ? ProgressEnd : ProgressStart + (ProgressEnd - ProgressStart) * (time - StartTime) / Math.Max(1e-4f, EndTime - StartTime);
    }

    public enum ArmorType : byte { None = 0, Light = 1, Heavy = 2, Regen = 3 }

    /// <summary>What a player took into a round, at the barrier drop.</summary>
    public sealed class PlayerLoadout
    {
        public byte Player = 255;
        public string Primary = "", Sidearm = "";   // gun classes, e.g. "AssaultRifle_AK", "LugerPistol"; "" = none
        public ArmorType Armor;
        public int Credits = -1;                    // credits left after buying (-1 = unknown)
        public int RoundStartCredits = -1;          // credits at the start of the buy phase
    }

    /// <summary>A player's scoreboard and bank from Time on: cumulative counters, as the game shows them.</summary>
    public struct PlayerStatSample
    {
        public float Time;
        public byte Player;
        public int Credits, RoundStartCredits;
        public short Kills, Deaths, Assists;
        public int Score;                           // combat score; ACS = Score / rounds played
        public byte UltPoints;
    }

    /// <summary>An ability's charges from Time on, one sample per change.</summary>
    public struct AbilityChargeSample
    {
        public float Time;
        public byte Player;
        public string Slot;                         // "C", "Q", "E", "X"
        public string Ability;                      // equippable class, e.g. "Ability_Wushu_E_Dash"
        public byte Charges;                        // usable charges
        public byte TemporaryCharges;               // how many of Charges are temporary (recharged/granted mid-round)
        public float CooldownEnd;                   // replay time the running cooldown ends; -1 = not cooling down
    }

    /// <summary>An ability cast, from the game's per-round cast statistics: every ability, including ones that
    /// spawn no world object (dashes, heals, ults). Position = caster location at the cast.</summary>
    public sealed class AbilityCast
    {
        public float Time;
        public byte Player = 255;
        public string Slot = "";               // "C", "Q", "E", "X"; "" = other (e.g. Astra's star placement)
        public short Round = -1;
        public float X, Y, Z;
    }

    public struct PathPoint { public float Time, X, Y, Z; }

    public struct Frame
    {
        public float X, Y, Z;          // position in VrpReplay.Space (Unreal cm / Unity m)
        public short YawCenti;         // yaw in 1/100 degree, [-18000, 18000)
        public short PitchCenti;       // pitch in 1/100 degree, up is positive
        public FrameFlags Flags;
        public byte Health;            // health incl. overheal, 0 when dead
        public byte Armor;             // remaining armor (heavy 50, light 25, regen 25)
        public byte Shield;            // ability shield
        public byte Weapon;            // index into VrpReplay.Equippables of what is in hand, 0 = nothing
        public byte Ammo;              // magazine of the weapon in hand, 255 = none/unknown
        public byte ReserveAmmo;       // reserve of the weapon in hand, capped at 253, 254 = infinite, 255 = none/unknown
        public const byte InfiniteReserve = 254;
        public bool HasInfiniteReserve => ReserveAmmo == InfiniteReserve;   // e.g. Neon's ult gun
        public float Yaw => YawCenti / 100f;
        public float Pitch => PitchCenti / 100f;
        public bool Alive => (Flags & FrameFlags.Alive) != 0;
        public bool Crouching => (Flags & FrameFlags.Crouching) != 0;
        public bool Walking => (Flags & FrameFlags.Walking) != 0;
        public bool Airborne => (Flags & FrameFlags.Airborne) != 0;
    }

    public sealed class Player
    {
        public string Puuid, AgentId, AgentClass;
        /// <summary>Riot ID ("Name#TAG") or any label you supply; "" if unknown. Replays carry no player names (only PUUIDs):
        /// fill it with vrfconv --names, ExtractOptions.PlayerNames or VrpReplay.ApplyNames.</summary>
        public string Name = "";
        /// <summary>The player's crosshair profile name (PlayerState.ProfileName, e.g. "Imported Crosshair 2"). Not a player name.</summary>
        public string CrosshairProfile = "";
        public string AgentName, AgentRole;   // e.g. "Neon", "Duelist" (see AgentCatalog.cs)
        public byte Team;
        public int PlayerId;
        /// <summary>Name if known, else the agent name.</summary>
        public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : AgentName ?? "";
    }

    public sealed class Team
    {
        public TeamRole InitialRole;
        public int FinalWins;
    }

    public sealed class Round
    {
        public int Number;
        public float StartTime, EndTime;
        public byte AttackingTeam = 255, WinningTeam = 255;
        public float PlantTime = -1f;
        public float PlantX, PlantY, PlantZ;
        public float DefuseTime = -1f;
        public RoundEndReason EndReason;
        public string Site = "";               // plant site "A"/"B"/"C" from the spike itself; "" = no plant
        public byte Planter = 255, Defuser = 255;
        public float BarrierTime = -1f;             // combat start (barrier drop); -1 = no combat phase
        public int[] TeamLoadoutValue = new int[0]; // per team index, at the barrier drop
        public List<PlayerLoadout> Loadouts = new List<PlayerLoadout>();   // per player, at the barrier drop
    }

    /// <summary>One trigger pull: X/Y/Z is the fire origin (eye), Directions the unit vector of each pellet, Yaw/Pitch the
    /// first direction.</summary>
    public sealed class Shot
    {
        public float Time;
        public byte Player = 255;
        public string Weapon = "";             // gun class, e.g. "AssaultRifle_AK"
        public float X, Y, Z, Yaw, Pitch;
        public byte Projectiles = 1;           // pellets (shotguns, Classic burst)
        public short AmmoRemaining = -1;       // magazine after the shot
        public List<(float X, float Y, float Z)> Directions = new List<(float, float, float)>();
    }

    /// <summary>Round phase / timer / spike state from Time on. The timer counts down from Remaining.</summary>
    public struct RoundStateSample
    {
        public float Time, Remaining;
        public RoundPhase Phase;
        public SpikeState Spike;
    }

    public sealed class MatchEvent
    {
        public float Time;
        public MatchEventType Type;
        public byte Player = 255;              // UltimateUsed/UltimateEnded: who; otherwise 255
    }

    public sealed class Kill
    {
        public float Time;
        public byte Killer = 255, Victim = 255;
        public short Round = -1;
        public string Weapon = "";     // class of the killing weapon/ability, e.g. "AssaultRifle_AK"
        public bool Headshot;
    }

    /// <summary>One damage instance to a player.</summary>
    public sealed class DamageEvent
    {
        public float Time;
        public byte Attacker = 255, Victim = 255;   // player indices (255 = not a player, e.g. fall damage)
        public float Damage;                        // health+armor+shield removed
        public string Weapon = "";                  // weapon or ability class, e.g. "LugerPistol", "Ability_Phoenix_4_Molotov_Production"
        public HitRegion Region;
        public bool Killed;
    }

    /// <summary>A world object: ability objects (projectiles, smokes, mollies, walls, deployables) and the spike, with
    /// lifetime and spawn transform, plus caster, agent/slot, landing time and a movement path.</summary>
    public sealed class Entity
    {
        public string Class;
        public float SpawnTime, DespawnTime = -1f;
        public float X, Y, Z, Yaw;
        public byte Owner = 255;               // caster player index
        public string Agent = "", Slot = "";   // internal agent name and ability folder ("Q", "E", "X", "4", ...)
        public float ImpactTime = -1f;         // projectile stopped (landed/popped)
        public List<PathPoint> Path = new List<PathPoint>();   // positions over time; empty = static at X/Y/Z

        /// <summary>Position at a time along Path (linear), or the spawn position.</summary>
        public void PositionAt(float time, out float x, out float y, out float z)
        {
            x = X; y = Y; z = Z;
            if (Path.Count == 0) return;
            if (time <= Path[0].Time) { x = Path[0].X; y = Path[0].Y; z = Path[0].Z; return; }
            for (int i = 1; i < Path.Count; i++)
            {
                if (Path[i].Time < time) continue;
                var a = Path[i - 1]; var b = Path[i];
                float u = b.Time > a.Time ? (time - a.Time) / (b.Time - a.Time) : 1f;
                x = a.X + (b.X - a.X) * u; y = a.Y + (b.Y - a.Y) * u; z = a.Z + (b.Z - a.Z) * u;
                return;
            }
            var l = Path[Path.Count - 1]; x = l.X; y = l.Y; z = l.Z;
        }
    }

    public sealed class VrpReplay
    {
        public const uint Magic = 0x31505256; // "VRP1"
        /// <summary>Layout id: bumped whenever the layout changes. Files with another value are rejected (no upgrades); convert
        /// the .vrf again.</summary>
        public const ushort FormatVersion = 10;
        public const byte HolderWingman = 254, HolderNone = 255;

        public string MatchId = "", Map = "", Build = "";   // Map = level path, e.g. "/Game/Maps/Jam/Jam"
        public long RecordedAtTicks;
        public float Duration;
        public float TickRate;
        public int TickCount;
        /// <summary>Space of every position, direction and angle in this replay.</summary>
        public CoordinateSpace Space = CoordinateSpace.Unreal;
        public List<Player> Players = new List<Player>();
        public List<Team> Teams = new List<Team>();
        public List<Round> Rounds = new List<Round>();
        public List<Kill> Kills = new List<Kill>();
        public List<Entity> Entities = new List<Entity>();
        public List<DamageEvent> Damage = new List<DamageEvent>();
        /// <summary>Weapon/ability classes referenced by Frame.Weapon; index 0 is "" (nothing in hand).</summary>
        public List<string> Equippables = new List<string> { "" };
        public List<Shot> Shots = new List<Shot>();
        public List<RoundStateSample> RoundStates = new List<RoundStateSample>();
        public List<MatchEvent> Events = new List<MatchEvent>();
        public List<StatusEffect> StatusEffects = new List<StatusEffect>();
        public List<SpikeCarrierSample> SpikeCarriers = new List<SpikeCarrierSample>();
        public List<DefuseAttempt> DefuseAttempts = new List<DefuseAttempt>();
        public List<AbilityCast> AbilityCasts = new List<AbilityCast>();
        /// <summary>Scoreboard/bank samples, one per change, sorted by time.</summary>
        public List<PlayerStatSample> PlayerStats = new List<PlayerStatSample>();
        /// <summary>Ability charges and cooldowns, one sample per change, sorted by time.</summary>
        public List<AbilityChargeSample> AbilityCharges = new List<AbilityChargeSample>();
        /// <summary>Bookmarks: user tags from the sidecar (or added at runtime) and auto highlights, sorted by time.</summary>
        public List<ReplayTag> Tags = new List<ReplayTag>();
        /// <summary>Highlight settings the auto tags were computed with (written back by ToSidecar).</summary>
        public HighlightSettings Highlights = new HighlightSettings();
        /// <summary>TickCount * Players.Count frames, index = tick * Players.Count + player.</summary>
        public Frame[] Frames = Array.Empty<Frame>();

        /// <summary>Catalog entry for Map (display name, minimap transform, callouts); null if the map is newer than MapCatalog.cs.</summary>
        public MapInfo MapInfo => MapCatalog.Find(Map);

        /// <summary>"Lotus" (falls back to the level name, e.g. "Jam", for unknown maps).</summary>
        public string MapName => MapCatalog.DisplayName(Map);

        /// <summary>Site the spike was planted on in this round ("A", "B", "C"); null if no plant. From the spike itself
        /// (Round.Site), else the nearest callout.</summary>
        public string PlantSite(Round r)
        {
            if (r.PlantTime < 0) return null;
            if (!string.IsNullOrEmpty(r.Site)) return r.Site;
            ToUnrealXY(r.PlantX, r.PlantY, r.PlantZ, out float x, out float y);
            return MapInfo?.NearestCallout(x, y)?.SuperRegion;
        }

        /// <summary>Nearest named callout for a player at a time ("B Main"); null when dead, off-map or the map has no callouts.</summary>
        public Callout CalloutAt(int player, float time)
        {
            var f = Sample(player, time);
            if (!f.Alive || (f.Flags & FrameFlags.OffMap) != 0) return null;
            ToUnrealXY(f.X, f.Y, f.Z, out float x, out float y);
            return MapInfo?.NearestCallout(x, y);
        }

        /// <summary>Class of the weapon in hand in a frame, e.g. "AssaultRifle_AK"; "" for nothing.</summary>
        public string WeaponOf(in Frame f) => f.Weapon < Equippables.Count ? Equippables[f.Weapon] : "";

        /// <summary>Sets Player.Name from a PUUID -> name map (e.g. Riot IDs from match history). Returns how many matched.</summary>
        public int ApplyNames(IDictionary<string, string> namesByPuuid)
        {
            int n = 0;
            if (namesByPuuid == null) return 0;
            foreach (var p in Players)
                if (p.Puuid != null && namesByPuuid.TryGetValue(p.Puuid, out var name) && !string.IsNullOrEmpty(name)) { p.Name = name; n++; }
            return n;
        }

        /// <summary>A player's credits, K/D/A, combat score and ult points at a time; Time = -1 if nothing is known yet.</summary>
        public PlayerStatSample StatsAt(int player, float time)
        {
            var best = new PlayerStatSample { Time = -1f, Player = (byte)player, Credits = -1, RoundStartCredits = -1 };
            foreach (var s in PlayerStats)
            {
                if (s.Time > time) break;
                if (s.Player == player) best = s;
            }
            return best;
        }

        /// <summary>An ability's charges at a time: slot "C"/"Q"/"E"/"X"; Time = -1 if nothing is known.</summary>
        public AbilityChargeSample ChargesAt(int player, string slot, float time)
        {
            var best = new AbilityChargeSample { Time = -1f, Player = (byte)player, Slot = slot, Ability = "", CooldownEnd = -1f };
            foreach (var c in AbilityCharges)
            {
                if (c.Time > time) break;
                if (c.Player == player && c.Slot == slot) best = c;
            }
            return best;
        }

        /// <summary>Average combat score (ACS) at a time: Score / rounds completed so far.</summary>
        public float AverageCombatScore(int player, float time)
        {
            var s = StatsAt(player, time);
            int done = 0;
            foreach (var r in Rounds) if (r.EndTime <= time + 0.01f) done++;
            return s.Time < 0 ? 0f : s.Score / (float)Math.Max(1, done);
        }

        /// <summary>A player's loadout in a round, or null.</summary>
        public PlayerLoadout LoadoutOf(int roundIndex, int player)
        {
            if (roundIndex < 0 || roundIndex >= Rounds.Count) return null;
            foreach (var l in Rounds[roundIndex].Loadouts) if (l.Player == player) return l;
            return null;
        }

        /// <summary>Who holds the spike at a time: player index, HolderWingman or HolderNone.</summary>
        public byte SpikeHolderAt(float time)
        {
            byte h = HolderNone;
            foreach (var s in SpikeCarriers) { if (s.Time > time) break; h = s.Holder; }
            return h;
        }

        /// <summary>The defuse attempt in progress at a time, or null.</summary>
        public DefuseAttempt DefuseAt(float time)
        {
            foreach (var d in DefuseAttempts) if (time >= d.StartTime && time < d.EndTime) return d;
            return null;
        }

        /// <summary>Status effects (blinds and debuffs) active on a player at a time.</summary>
        public IEnumerable<StatusEffect> StatusEffectsAt(int player, float time)
        {
            foreach (var s in StatusEffects) if (s.Player == player && s.ActiveAt(time)) yield return s;
        }

        /// <summary>Round phase, remaining time on the round/buy/spike timer, and spike state at a time.</summary>
        public RoundStateSample RoundStateAt(float time)
        {
            int lo = 0, hi = RoundStates.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (RoundStates[mid].Time <= time) { idx = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (idx < 0) return new RoundStateSample { Time = time };
            var s = RoundStates[idx];
            s.Remaining = Math.Max(0f, s.Remaining - (time - s.Time));
            s.Time = time;
            return s;
        }

        public Frame GetFrame(int tick, int player) => Frames[tick * Players.Count + player];

        public int TickAt(float time) => Math.Max(0, Math.Min(TickCount - 1, (int)(time * TickRate)));

        /// <summary>Interpolated state of a player at an arbitrary time.</summary>
        public Frame Sample(int player, float time)
        {
            float f = time * TickRate;
            int t0 = Math.Max(0, Math.Min(TickCount - 1, (int)Math.Floor(f)));
            int t1 = Math.Min(TickCount - 1, t0 + 1);
            var a = GetFrame(t0, player);
            var b = GetFrame(t1, player);
            float u = Math.Max(0f, Math.Min(1f, f - t0));
            if (t0 == t1 || (b.Flags & FrameFlags.Snapped) != 0 || a.Alive != b.Alive) return u < 0.5f ? a : b;
            var r = a;
            r.X = a.X + (b.X - a.X) * u;
            r.Y = a.Y + (b.Y - a.Y) * u;
            r.Z = a.Z + (b.Z - a.Z) * u;
            float dy = ((b.YawCenti - a.YawCenti) % 36000 + 54000) % 36000 - 18000;
            float yaw = a.YawCenti + dy * u;
            if (yaw >= 18000) yaw -= 36000; else if (yaw < -18000) yaw += 36000;
            r.YawCenti = (short)yaw;
            r.PitchCenti = (short)(a.PitchCenti + (b.PitchCenti - a.PitchCenti) * u);
            return r;
        }

        public int RoundAt(float time)
        {
            for (int i = Rounds.Count - 1; i >= 0; i--)
                if (time >= Rounds[i].StartTime) return i;
            return Rounds.Count > 0 ? 0 : -1;
        }

        public TeamRole RoleOf(int team, int roundIndex)
        {
            if (roundIndex < 0 || roundIndex >= Rounds.Count || Rounds[roundIndex].AttackingTeam == 255) return TeamRole.Unknown;
            return Rounds[roundIndex].AttackingTeam == team ? TeamRole.Attacking : TeamRole.Defending;
        }

        // ---------------------------------------------------------------- anonymizing
        /// <summary>Smallest key Anonymize accepts (bytes); use 32 random bytes.</summary>
        public const int MinAnonymizeKeyBytes = 16;

        /// <summary>
        /// Removes what identifies the players and the match, for publishing a replay:
        /// <list type="bullet">
        /// <item>PUUIDs and the match id become keyed pseudonyms (<see cref="Pseudonym"/>). The same account gets the same
        /// pseudonym in every file anonymized with the same key, so a player can be followed across matches by pseudonym,
        /// but without the key it cannot be turned back into the account, and the match cannot be looked up by id.</item>
        /// <item>Player names become generated ones (<see cref="GeneratedName"/>, e.g. "Swift Otter 427"), made from the
        /// pseudonym, so the same player has the same name in every match; or are cleared with
        /// <paramref name="generateNames"/> false. Crosshair profile names (text the player typed) are cleared.</item>
        /// <item>The recording time is cleared (it would find the match on a match-history site).</item>
        /// <item>User tags are dropped unless <paramref name="keepUserTags"/>: their labels and notes are free text. Auto
        /// highlights stay, with any real player name in them replaced by the new name (or the agent name).</item>
        /// </list>
        /// What remains (map, agents, the round timeline) can still single out a match for someone with match-history data.
        /// Apply tags first: a sidecar names players by PUUID.
        /// </summary>
        public void Anonymize(byte[] key, bool keepUserTags = false, bool generateNames = true)
        {
            if (key == null || key.Length < MinAnonymizeKeyBytes)
                throw new ArgumentException($"the anonymization key must be at least {MinAnonymizeKeyBytes} bytes", nameof(key));
            MatchId = Pseudonym(key, "match", MatchId);
            RecordedAtTicks = 0;
            if (!keepUserTags) Tags.RemoveAll(t => t.Source == TagSource.User);

            var pseudonyms = new List<string>(Players.Count);
            foreach (var p in Players) pseudonyms.Add(Pseudonym(key, "player", p.Puuid));
            var names = generateNames ? GeneratedNames(pseudonyms) : null;
            for (int i = 0; i < Players.Count; i++)
            {
                var p = Players[i];
                string replacement = names != null && names[i].Length > 0 ? names[i] : p.AgentName ?? "";
                // highlight labels use the display name ("First blood - Name#TAG" when names were supplied)
                if (!string.IsNullOrEmpty(p.Name))
                    foreach (var t in Tags)
                    {
                        if (!string.IsNullOrEmpty(t.Label)) t.Label = t.Label.Replace(p.Name, replacement);
                        if (!string.IsNullOrEmpty(t.Note)) t.Note = t.Note.Replace(p.Name, replacement);
                    }
                p.Puuid = pseudonyms[i];
                p.Name = names != null ? names[i] : "";
                p.CrosshairProfile = "";
            }
        }

        /// <summary>
        /// A readable, made-up player name for a pseudonym: "Adjective Animal NNN" (number 10-999). The same pseudonym
        /// always gives the same name. It never has Riot's "#TAG", so it can't be mistaken for a real player's Riot ID.
        /// 128 x 128 x 990, about 16 million names; two players sharing one is unlikely but possible (<see cref="GeneratedNames"/>
        /// settles it within a match). "" for "".
        /// </summary>
        public static string GeneratedName(string pseudonym) => GeneratedName(pseudonym, 0);

        private static string GeneratedName(string pseudonym, int attempt)
        {
            if (string.IsNullOrEmpty(pseudonym)) return "";
            // the pseudonym is already a keyed hash; hash it again with the attempt so any length of input spreads evenly
            byte[] h;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                h = sha.ComputeHash(Encoding.UTF8.GetBytes($"name:{attempt}:{pseudonym}"));
            uint U(int at) => (uint)(h[at] | h[at + 1] << 8 | h[at + 2] << 16 | h[at + 3] << 24);
            return $"{NameAdjectives[U(0) % (uint)NameAdjectives.Length]} {NameAnimals[U(4) % (uint)NameAnimals.Length]} {10 + U(8) % 990}";
        }

        /// <summary>Generated names for the players of one match, all different: a player whose name is taken by an earlier
        /// player in the list draws again (deterministic, but that player may then differ from their name elsewhere).</summary>
        public static List<string> GeneratedNames(IList<string> pseudonyms)
        {
            var result = new List<string>(pseudonyms.Count);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ps in pseudonyms)
            {
                string name = GeneratedName(ps, 0);
                for (int attempt = 1; name.Length > 0 && !taken.Add(name); attempt++) name = GeneratedName(ps, attempt);
                result.Add(name);
            }
            return result;
        }

        // FROZEN: a name is picked by position in these lists, so changing, reordering or adding a word renames every player
        // in every published replay. For new lists, add a second version and keep these. Chosen to be neutral: no agent names,
        // nothing that combines into an insult.
        public static IReadOnlyList<string> GeneratedNameAdjectives => NameAdjectives;
        public static IReadOnlyList<string> GeneratedNameAnimals => NameAnimals;

        private static readonly string[] NameAdjectives =
        {
            "Agile", "Alpine", "Amber", "Ancient", "Arctic", "Azure", "Balmy", "Bold", "Brave", "Breezy",
            "Bright", "Brisk", "Calm", "Candid", "Cheery", "Clever", "Coastal", "Cobalt", "Cosmic", "Crimson",
            "Crisp", "Curious", "Dapper", "Daring", "Dashing", "Deft", "Desert", "Dreamy", "Dusky", "Eager",
            "Early", "Electric", "Emerald", "Fabled", "Fair", "Fancy", "Fearless", "Fiery", "Fluent", "Frosty",
            "Gentle", "Gilded", "Glad", "Golden", "Grand", "Granite", "Hardy", "Hazel", "Hidden", "Honest",
            "Humble", "Icy", "Indigo", "Ivory", "Jade", "Jolly", "Keen", "Kind", "Lively", "Lucky",
            "Lunar", "Mellow", "Merry", "Mighty", "Misty", "Modest", "Mossy", "Nimble", "Noble", "Northern",
            "Oaken", "Olive", "Patient", "Plucky", "Polar", "Proud", "Quick", "Quiet", "Radiant", "Rapid",
            "Ready", "Rocky", "Royal", "Rustic", "Sandy", "Scarlet", "Shady", "Sharp", "Silent", "Silver",
            "Sleek", "Smooth", "Snowy", "Solar", "Solid", "Sonic", "Spry", "Steady", "Stellar", "Stormy",
            "Sturdy", "Sunny", "Swift", "Tidy", "Tranquil", "True", "Trusty", "Twilight", "Valiant", "Velvet",
            "Vivid", "Wandering", "Warm", "Wary", "Wild", "Windy", "Wise", "Witty", "Woolly", "Zesty",
            "Zippy", "Bouncy", "Dauntless", "Coral", "Dusty", "Feathered", "Hushed", "Lofty",
        };

        private static readonly string[] NameAnimals =
        {
            "Albatross", "Alpaca", "Antelope", "Armadillo", "Badger", "Barracuda", "Beaver", "Bison", "Bobcat", "Buffalo",
            "Camel", "Capybara", "Caribou", "Cheetah", "Chinchilla", "Condor", "Cougar", "Coyote", "Crane", "Cricket",
            "Dingo", "Dolphin", "Dove", "Dragonfly", "Eagle", "Egret", "Elk", "Emu", "Falcon", "Ferret",
            "Finch", "Flamingo", "Fox", "Gazelle", "Gibbon", "Giraffe", "Gopher", "Grizzly", "Grouse", "Hare",
            "Hawk", "Hedgehog", "Heron", "Hippo", "Hornet", "Husky", "Ibex", "Ibis", "Iguana", "Impala",
            "Jackal", "Jaguar", "Jay", "Kestrel", "Kingfisher", "Kiwi", "Koala", "Kudu", "Lemur", "Leopard",
            "Llama", "Lobster", "Lynx", "Macaw", "Magpie", "Mallard", "Manatee", "Mantis", "Marmot", "Marten",
            "Meerkat", "Mink", "Mongoose", "Moose", "Narwhal", "Newt", "Ocelot", "Octopus", "Orca", "Oriole",
            "Osprey", "Ostrich", "Otter", "Owl", "Panda", "Panther", "Parrot", "Pelican", "Penguin", "Pheasant",
            "Pigeon", "Puffin", "Puma", "Quail", "Rabbit", "Raccoon", "Raven", "Reindeer", "Robin", "Salmon",
            "Seal", "Shark", "Sparrow", "Squid", "Squirrel", "Stag", "Starling", "Stingray", "Stork", "Swallow",
            "Swan", "Tapir", "Tiger", "Toucan", "Trout", "Turtle", "Vulture", "Wallaby", "Walrus", "Weasel",
            "Whale", "Wolf", "Wolverine", "Wombat", "Yak", "Zebra", "Bluebird", "Cardinal",
        };

        /// <summary>Keyed pseudonym of an identifier: HMAC-SHA256 of "kind:value" (value lower-cased) under the key, first 96
        /// bits as 24 hex digits. "" stays "". The kind keeps a player and a match with the same id apart.</summary>
        public static string Pseudonym(byte[] key, string kind, string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            using (var hmac = new System.Security.Cryptography.HMACSHA256(key))
            {
                var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(kind + ":" + value.ToLowerInvariant()));
                var sb = new StringBuilder(24);
                for (int i = 0; i < 12; i++) sb.Append(mac[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // ---------------------------------------------------------------- tags
        /// <summary>Replaces Tags: resolves the sidecar's user tags and, if highlights is set and the sidecar enables them,
        /// appends Highlights.Detect. Entries that do not resolve (missing round, no barrier drop) are skipped and reported
        /// in warnings, together with the sidecar's own parse warnings. A null sidecar means no user tags, default settings.</summary>
        public void ApplyTags(TagSidecar sidecar, bool highlights, out List<string> warnings)
        {
            warnings = new List<string>();
            Tags.Clear();
            Highlights = sidecar?.Highlights?.Clone() ?? new HighlightSettings();
            if (sidecar != null)
            {
                warnings.AddRange(sidecar.Warnings);
                for (int i = 0; i < sidecar.Tags.Count; i++)
                {
                    var spec = sidecar.Tags[i];
                    string what = $"tag #{i + 1}{(spec.Label.Length > 0 ? $" ('{spec.Label}')" : "")}";
                    if (spec.Anchor == null) { warnings.Add($"{what}: no anchor; skipped"); continue; }
                    if (!spec.Anchor.Resolve(this, out float t, out string err)) { warnings.Add($"{what}: {err}; skipped"); continue; }
                    var tag = new ReplayTag
                    {
                        Time = t, Duration = Math.Max(0f, Math.Min(spec.Duration, Duration - t)), Label = spec.Label, Note = spec.Note,
                        Categories = new List<string>(spec.Categories), Source = TagSource.User, Anchor = spec.Anchor.ToString(),
                        Round = (short)(spec.Anchor.Kind != TagAnchorKind.Match ? spec.Anchor.Round - 1 : RoundAt(t))
                    };
                    if (spec.PlayerPuuid.Length > 0)
                    {
                        int p = Players.FindIndex(x => x.Puuid == spec.PlayerPuuid);
                        if (p >= 0) tag.Player = (byte)p; else warnings.Add($"{what}: player {spec.PlayerPuuid} is not in this match; tag kept without a player");
                    }
                    else if (spec.PlayerIndex >= 0)
                    {
                        if (spec.PlayerIndex < Players.Count) tag.Player = (byte)spec.PlayerIndex;
                        else warnings.Add($"{what}: player index {spec.PlayerIndex} is out of range (0..{Players.Count - 1}); tag kept without a player");
                    }
                    Tags.Add(tag);
                }
            }
            if (highlights)
                foreach (var a in ValorantReplay.Playback.Highlights.Detect(this, Highlights))
                {
                    // an auto tag the user already kept as their own (ReplayTag.MakeUserTag) is not added twice
                    string cat = a.Kind.ToLowerInvariant();
                    if (!Tags.Exists(u => u.Source == TagSource.User && Math.Abs(u.Time - a.Time) < 0.05f && u.Categories.Contains(cat))) Tags.Add(a);
                }
            SortTags();
        }

        /// <summary>The user tags and highlight settings as a sidecar, ready to Save. Auto tags are not included (they are
        /// regenerated). A tag keeps its anchor form (round/barrier/match, clock or seconds); if its Time was changed, the
        /// anchor is moved to the new time.</summary>
        public TagSidecar ToSidecar()
        {
            var sc = new TagSidecar { Highlights = (Highlights ?? new HighlightSettings()).Clone() };
            foreach (var t in Tags)
            {
                if (t.Source != TagSource.User) continue;
                var a = TagAnchor.Parse(t.Anchor);
                if (a == null) a = TagAnchor.AtTime(t.Time);
                else if (!a.Resolve(this, out float at, out _) || Math.Abs(at - t.Time) > 0.01f) a = a.Rebase(this, t.Time);
                sc.Tags.Add(new TagSpec
                {
                    Anchor = a, Duration = t.Duration, Label = t.Label ?? "", Note = t.Note ?? "", Categories = new List<string>(t.Categories),
                    PlayerPuuid = t.Player < Players.Count ? Players[t.Player].Puuid ?? "" : "",
                    PlayerIndex = t.Player < Players.Count && string.IsNullOrEmpty(Players[t.Player].Puuid) ? t.Player : -1
                });
            }
            return sc;
        }

        /// <summary>Adds a tag, keeping Tags sorted by time.</summary>
        public void AddTag(ReplayTag tag)
        {
            int i = Tags.Count;
            while (i > 0 && Tags[i - 1].Time > tag.Time) i--;
            Tags.Insert(i, tag);
        }

        /// <summary>Resolves an anchor (TagAnchor.AtTime / InRound / FromBarrier) and adds a user tag there. Throws
        /// ArgumentException if the anchor does not resolve.</summary>
        public ReplayTag AddTag(TagAnchor anchor, string label = "", float duration = 0f, int player = 255)
        {
            if (!anchor.Resolve(this, out float t, out string err)) throw new ArgumentException(err, nameof(anchor));
            var tag = new ReplayTag
            {
                Time = t, Duration = Math.Max(0f, duration), Label = label ?? "", Player = (byte)Math.Min(255, Math.Max(0, player)), Source = TagSource.User,
                Anchor = anchor.ToString(), Round = (short)(anchor.Kind != TagAnchorKind.Match ? anchor.Round - 1 : RoundAt(t))
            };
            AddTag(tag);
            return tag;
        }

        public bool RemoveTag(ReplayTag tag) => Tags.Remove(tag);

        /// <summary>Restores time order after tag times were edited in place.</summary>
        public void SortTags()
        {
            // stable: equal times keep their order
            var order = new Dictionary<ReplayTag, int>();
            for (int i = 0; i < Tags.Count; i++) order[Tags[i]] = i;
            Tags.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : order[a].CompareTo(order[b]));
        }

        /// <summary>Tags of a round (0-based index).</summary>
        public IEnumerable<ReplayTag> TagsInRound(int roundIndex)
        {
            foreach (var t in Tags) if (t.Round == roundIndex) yield return t;
        }

        /// <summary>Tags that overlap [t0, t1] (points inside it, ranges that intersect it).</summary>
        public IEnumerable<ReplayTag> TagsBetween(float t0, float t1)
        {
            foreach (var t in Tags) if (t.Time <= t1 && t.EndTime >= t0) yield return t;
        }

        /// <summary>The first tag starting after time (for "jump to next"); null if none.</summary>
        public ReplayTag NextTag(float time, float epsilon = 0.05f)
        {
            foreach (var t in Tags) if (t.Time > time + epsilon) return t;
            return null;
        }

        /// <summary>The last tag starting before time (for "jump to previous"); null if none.</summary>
        public ReplayTag PrevTag(float time, float epsilon = 0.05f)
        {
            for (int i = Tags.Count - 1; i >= 0; i--) if (Tags[i].Time < time - epsilon) return Tags[i];
            return null;
        }

        // ---------------------------------------------------------------- coordinate helpers
        /// <summary>Unreal (cm, Z-up, X forward, Y right) -> Unity (m, Y-up, Z forward, X right).</summary>
        public static void ToUnity(float ueX, float ueY, float ueZ, out float x, out float y, out float z)
        {
            x = ueY * 0.01f; y = ueZ * 0.01f; z = ueX * 0.01f;
        }

        /// <summary>A position in this replay's space -> Unreal X/Y (cm), as used by MapCatalog (minimap, callouts).</summary>
        public void ToUnrealXY(float x, float y, float z, out float ueX, out float ueY)
        {
            if (Space == CoordinateSpace.Unity) { ueX = z * 100f; ueY = x * 100f; }
            else { ueX = x; ueY = y; }
        }

        /// <summary>
        /// Converts every position, direction and angle from Unreal to Unity space in place (no-op if already Unity).
        /// vrfconv does this by default; use it on Unreal-space files (vrfconv --unreal).
        /// </summary>
        public void ConvertToUnitySpace()
        {
            if (Space == CoordinateSpace.Unity) return;
            for (int i = 0; i < Frames.Length; i++)
            {
                ref var f = ref Frames[i];
                ToUnity(f.X, f.Y, f.Z, out f.X, out f.Y, out f.Z);
                f.PitchCenti = (short)-f.PitchCenti;
            }
            foreach (var r in Rounds) ToUnity(r.PlantX, r.PlantY, r.PlantZ, out r.PlantX, out r.PlantY, out r.PlantZ);
            foreach (var c in AbilityCasts) ToUnity(c.X, c.Y, c.Z, out c.X, out c.Y, out c.Z);
            foreach (var e in Entities)
            {
                ToUnity(e.X, e.Y, e.Z, out e.X, out e.Y, out e.Z);
                for (int i = 0; i < e.Path.Count; i++)
                {
                    var p = e.Path[i];
                    ToUnity(p.X, p.Y, p.Z, out p.X, out p.Y, out p.Z);
                    e.Path[i] = p;
                }
            }
            foreach (var s in Shots)
            {
                ToUnity(s.X, s.Y, s.Z, out s.X, out s.Y, out s.Z);
                s.Pitch = -s.Pitch;
                for (int i = 0; i < s.Directions.Count; i++)
                {
                    var d = s.Directions[i];
                    s.Directions[i] = (d.Y, d.Z, d.X);      // unit vector: axis swap, no scale
                }
            }
            Space = CoordinateSpace.Unity;
        }

#if UNITY_5_3_OR_NEWER
        /// <summary>Unity position of a frame, whatever this replay's Space is.</summary>
        public Vector3 UnityPosition(in Frame f) => UnityPoint(f.X, f.Y, f.Z);
        /// <summary>Unity rotation (yaw + pitch) of a frame, whatever this replay's Space is.</summary>
        public Quaternion UnityRotation(in Frame f) => Quaternion.Euler(Space == CoordinateSpace.Unity ? f.Pitch : -f.Pitch, f.Yaw, 0f);
        /// <summary>A point in this replay's space as a Unity position.</summary>
        public Vector3 UnityPoint(float x, float y, float z) =>
            Space == CoordinateSpace.Unity ? new Vector3(x, y, z) : new Vector3(y * 0.01f, z * 0.01f, x * 0.01f);
        /// <summary>A direction in this replay's space (e.g. Shot.Directions) as a Unity direction.</summary>
        public Vector3 UnityDirection(float x, float y, float z) =>
            Space == CoordinateSpace.Unity ? new Vector3(x, y, z) : new Vector3(y, z, x);
#endif

        // ---------------------------------------------------------------- IO
        // Layout: u32 Magic, u16 FormatVersion, then a Deflate stream in exactly the order of Save below. There is no
        // compatibility with other layouts: a file with a different FormatVersion is rejected; convert the .vrf again.
        public static VrpReplay Load(string path)
        {
            using (var fs = File.OpenRead(path)) return Load(fs);
        }

        public static VrpReplay Load(Stream s)
        {
            var head = new BinaryReader(s, Encoding.UTF8, true);
            if (head.ReadUInt32() != Magic) throw new InvalidDataException("not a .vrp file");
            ushort ver = head.ReadUInt16();
            if (ver != FormatVersion)
                throw new InvalidDataException($".vrp format {ver} is not the supported format {FormatVersion}: convert the .vrf again with this version of vrfconv");
            using (var z = new DeflateStream(s, CompressionMode.Decompress, true))
            using (var r = new BinaryReader(z, Encoding.UTF8))
            {
                var v = new VrpReplay
                {
                    MatchId = Str(r), Map = Str(r), Build = Str(r), RecordedAtTicks = r.ReadInt64(), Duration = r.ReadSingle(),
                    TickRate = r.ReadSingle(), TickCount = r.ReadInt32(), Space = (CoordinateSpace)r.ReadByte()
                };
                int np = r.ReadByte();
                for (int i = 0; i < np; i++)
                    v.Players.Add(new Player
                    {
                        Puuid = Str(r), Name = Str(r), CrosshairProfile = Str(r), AgentId = Str(r), AgentClass = Str(r), AgentName = Str(r),
                        AgentRole = Str(r), Team = r.ReadByte(), PlayerId = r.ReadInt32()
                    });
                int nt = r.ReadByte();
                for (int i = 0; i < nt; i++)
                    v.Teams.Add(new Team { InitialRole = (TeamRole)r.ReadByte(), FinalWins = r.ReadInt32() });
                int nr = r.ReadUInt16();
                for (int i = 0; i < nr; i++)
                {
                    var rd = new Round
                    {
                        Number = r.ReadInt16(), StartTime = r.ReadSingle(), EndTime = r.ReadSingle(), BarrierTime = r.ReadSingle(),
                        AttackingTeam = r.ReadByte(), WinningTeam = r.ReadByte(), EndReason = (RoundEndReason)r.ReadByte(),
                        PlantTime = r.ReadSingle(), PlantX = r.ReadSingle(), PlantY = r.ReadSingle(), PlantZ = r.ReadSingle(), Site = Str(r),
                        Planter = r.ReadByte(), DefuseTime = r.ReadSingle(), Defuser = r.ReadByte(), TeamLoadoutValue = new int[r.ReadByte()]
                    };
                    for (int t = 0; t < rd.TeamLoadoutValue.Length; t++) rd.TeamLoadoutValue[t] = r.ReadInt32();
                    int nl = r.ReadByte();
                    for (int j = 0; j < nl; j++)
                        rd.Loadouts.Add(new PlayerLoadout
                        {
                            Player = r.ReadByte(), Primary = Str(r), Sidearm = Str(r), Armor = (ArmorType)r.ReadByte(), Credits = r.ReadInt32(),
                            RoundStartCredits = r.ReadInt32()
                        });
                    v.Rounds.Add(rd);
                }
                int nk = r.ReadUInt16();
                for (int i = 0; i < nk; i++)
                    v.Kills.Add(new Kill
                    {
                        Time = r.ReadSingle(), Killer = r.ReadByte(), Victim = r.ReadByte(), Round = r.ReadInt16(), Weapon = Str(r), Headshot = r.ReadBoolean()
                    });
                int nd = r.ReadInt32();
                for (int i = 0; i < nd; i++)
                    v.Damage.Add(new DamageEvent
                    {
                        Time = r.ReadSingle(), Attacker = r.ReadByte(), Victim = r.ReadByte(), Damage = r.ReadSingle(), Weapon = Str(r),
                        Region = (HitRegion)r.ReadByte(), Killed = r.ReadBoolean()
                    });
                int ne = r.ReadInt32();
                for (int i = 0; i < ne; i++)
                {
                    var e = new Entity
                    {
                        Class = Str(r), SpawnTime = r.ReadSingle(), DespawnTime = r.ReadSingle(), X = r.ReadSingle(), Y = r.ReadSingle(),
                        Z = r.ReadSingle(), Yaw = r.ReadSingle(), Owner = r.ReadByte(), Agent = Str(r), Slot = Str(r), ImpactTime = r.ReadSingle()
                    };
                    int npp = r.ReadUInt16();
                    for (int j = 0; j < npp; j++) e.Path.Add(new PathPoint { Time = r.ReadSingle(), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle() });
                    v.Entities.Add(e);
                }
                int neq = r.ReadByte();
                v.Equippables.Clear();
                for (int i = 0; i < neq; i++) v.Equippables.Add(Str(r));
                int ns = r.ReadInt32();
                for (int i = 0; i < ns; i++)
                {
                    var shot = new Shot
                    {
                        Time = r.ReadSingle(), Player = r.ReadByte(), Weapon = Str(r), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(),
                        Yaw = r.ReadSingle(), Pitch = r.ReadSingle(), Projectiles = r.ReadByte(), AmmoRemaining = r.ReadInt16()
                    };
                    int ndir = r.ReadByte();
                    for (int j = 0; j < ndir; j++) shot.Directions.Add((r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                    v.Shots.Add(shot);
                }
                int nrs = r.ReadInt32();
                for (int i = 0; i < nrs; i++)
                    v.RoundStates.Add(new RoundStateSample { Time = r.ReadSingle(), Remaining = r.ReadSingle(), Phase = (RoundPhase)r.ReadByte(), Spike = (SpikeState)r.ReadByte() });
                int nev = r.ReadInt32();
                for (int i = 0; i < nev; i++)
                    v.Events.Add(new MatchEvent { Time = r.ReadSingle(), Type = (MatchEventType)r.ReadByte(), Player = r.ReadByte() });
                int nse = r.ReadInt32();
                for (int i = 0; i < nse; i++)
                    v.StatusEffects.Add(new StatusEffect
                    {
                        Time = r.ReadSingle(), Duration = r.ReadSingle(), Player = r.ReadByte(), SourcePlayer = r.ReadByte(),
                        Type = (StatusEffectType)r.ReadByte(), Source = r.ReadInt32(), Name = Str(r)
                    });
                int nc = r.ReadInt32();
                for (int i = 0; i < nc; i++) v.SpikeCarriers.Add(new SpikeCarrierSample { Time = r.ReadSingle(), Holder = r.ReadByte() });
                int nda = r.ReadInt32();
                for (int i = 0; i < nda; i++)
                    v.DefuseAttempts.Add(new DefuseAttempt
                    {
                        StartTime = r.ReadSingle(), EndTime = r.ReadSingle(), Player = r.ReadByte(), ProgressStart = r.ReadSingle(),
                        ProgressEnd = r.ReadSingle(), Defused = r.ReadBoolean()
                    });
                int nac = r.ReadInt32();
                for (int i = 0; i < nac; i++)
                    v.AbilityCasts.Add(new AbilityCast
                    {
                        Time = r.ReadSingle(), Player = r.ReadByte(), Slot = Str(r), Round = r.ReadInt16(), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle()
                    });
                int nch = r.ReadInt32();
                for (int i = 0; i < nch; i++)
                    v.AbilityCharges.Add(new AbilityChargeSample
                    {
                        Time = r.ReadSingle(), Player = r.ReadByte(), Slot = Str(r), Ability = Str(r), Charges = r.ReadByte(),
                        TemporaryCharges = r.ReadByte(), CooldownEnd = r.ReadSingle()
                    });
                int nps = r.ReadInt32();
                for (int i = 0; i < nps; i++)
                    v.PlayerStats.Add(new PlayerStatSample
                    {
                        Time = r.ReadSingle(), Player = r.ReadByte(), Credits = r.ReadInt32(), RoundStartCredits = r.ReadInt32(),
                        Kills = r.ReadInt16(), Deaths = r.ReadInt16(), Assists = r.ReadInt16(), Score = r.ReadInt32(), UltPoints = r.ReadByte()
                    });
                v.Highlights = new HighlightSettings
                {
                    Enabled = r.ReadBoolean(), ClutchMin = r.ReadByte(), ClutchRequireWin = r.ReadBoolean(), Ace = r.ReadBoolean(),
                    MultiKill = r.ReadByte(), MultiKillWindow = r.ReadSingle(), FirstBlood = r.ReadBoolean()
                };
                int ntg = r.ReadInt32();
                for (int i = 0; i < ntg; i++)
                {
                    var tag = new ReplayTag
                    {
                        Time = r.ReadSingle(), Duration = r.ReadSingle(), Round = r.ReadInt16(), Player = r.ReadByte(), Label = Str(r), Note = Str(r)
                    };
                    int ncat = r.ReadByte();
                    for (int j = 0; j < ncat; j++) tag.Categories.Add(Str(r));
                    tag.Source = (TagSource)r.ReadByte(); tag.Kind = Str(r); tag.Anchor = Str(r);
                    v.Tags.Add(tag);
                }
                v.Frames = new Frame[v.TickCount * np];
                for (int i = 0; i < v.Frames.Length; i++)
                    v.Frames[i] = new Frame
                    {
                        X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), YawCenti = r.ReadInt16(), PitchCenti = r.ReadInt16(),
                        Flags = (FrameFlags)r.ReadByte(), Health = r.ReadByte(), Armor = r.ReadByte(), Shield = r.ReadByte(),
                        Weapon = r.ReadByte(), Ammo = r.ReadByte(), ReserveAmmo = r.ReadByte()
                    };
                return v;
            }
        }

        public void Save(string path)
        {
            using (var fs = File.Create(path)) Save(fs);
        }

        public void Save(Stream s)
        {
            var head = new BinaryWriter(s, Encoding.UTF8, true);
            head.Write(Magic);
            head.Write(FormatVersion);
            head.Flush();
            using (var z = new DeflateStream(s, System.IO.Compression.CompressionLevel.Optimal, true))
            using (var w = new BinaryWriter(z, Encoding.UTF8))
            {
                Str(w, MatchId); Str(w, Map); Str(w, Build);
                w.Write(RecordedAtTicks); w.Write(Duration); w.Write(TickRate); w.Write(TickCount); w.Write((byte)Space);
                w.Write((byte)Players.Count);
                foreach (var p in Players)
                {
                    Str(w, p.Puuid); Str(w, p.Name); Str(w, p.CrosshairProfile); Str(w, p.AgentId); Str(w, p.AgentClass); Str(w, p.AgentName);
                    Str(w, p.AgentRole); w.Write(p.Team); w.Write(p.PlayerId);
                }
                w.Write((byte)Teams.Count);
                foreach (var t in Teams) { w.Write((byte)t.InitialRole); w.Write(t.FinalWins); }
                w.Write((ushort)Rounds.Count);
                foreach (var rd in Rounds)
                {
                    w.Write((short)rd.Number); w.Write(rd.StartTime); w.Write(rd.EndTime); w.Write(rd.BarrierTime);
                    w.Write(rd.AttackingTeam); w.Write(rd.WinningTeam); w.Write((byte)rd.EndReason);
                    w.Write(rd.PlantTime); w.Write(rd.PlantX); w.Write(rd.PlantY); w.Write(rd.PlantZ); Str(w, rd.Site); w.Write(rd.Planter);
                    w.Write(rd.DefuseTime); w.Write(rd.Defuser);
                    w.Write((byte)rd.TeamLoadoutValue.Length);
                    foreach (var tv in rd.TeamLoadoutValue) w.Write(tv);
                    w.Write((byte)rd.Loadouts.Count);
                    foreach (var l in rd.Loadouts)
                    {
                        w.Write(l.Player); Str(w, l.Primary); Str(w, l.Sidearm); w.Write((byte)l.Armor); w.Write(l.Credits); w.Write(l.RoundStartCredits);
                    }
                }
                w.Write((ushort)Kills.Count);
                foreach (var k in Kills) { w.Write(k.Time); w.Write(k.Killer); w.Write(k.Victim); w.Write(k.Round); Str(w, k.Weapon); w.Write(k.Headshot); }
                w.Write(Damage.Count);
                foreach (var d in Damage) { w.Write(d.Time); w.Write(d.Attacker); w.Write(d.Victim); w.Write(d.Damage); Str(w, d.Weapon); w.Write((byte)d.Region); w.Write(d.Killed); }
                w.Write(Entities.Count);
                foreach (var e in Entities)
                {
                    Str(w, e.Class); w.Write(e.SpawnTime); w.Write(e.DespawnTime); w.Write(e.X); w.Write(e.Y); w.Write(e.Z); w.Write(e.Yaw);
                    w.Write(e.Owner); Str(w, e.Agent); Str(w, e.Slot); w.Write(e.ImpactTime);
                    int npp = Math.Min(ushort.MaxValue, e.Path.Count);
                    w.Write((ushort)npp);
                    for (int j = 0; j < npp; j++) { w.Write(e.Path[j].Time); w.Write(e.Path[j].X); w.Write(e.Path[j].Y); w.Write(e.Path[j].Z); }
                }
                if (Equippables.Count > 255) throw new InvalidOperationException("more than 255 distinct equippables");
                w.Write((byte)Equippables.Count);
                foreach (var e in Equippables) Str(w, e);
                w.Write(Shots.Count);
                foreach (var x in Shots)
                {
                    w.Write(x.Time); w.Write(x.Player); Str(w, x.Weapon); w.Write(x.X); w.Write(x.Y); w.Write(x.Z); w.Write(x.Yaw); w.Write(x.Pitch);
                    w.Write(x.Projectiles); w.Write(x.AmmoRemaining);
                    int ndir = Math.Min(255, x.Directions.Count);
                    w.Write((byte)ndir);
                    for (int j = 0; j < ndir; j++) { w.Write(x.Directions[j].X); w.Write(x.Directions[j].Y); w.Write(x.Directions[j].Z); }
                }
                w.Write(RoundStates.Count);
                foreach (var x in RoundStates) { w.Write(x.Time); w.Write(x.Remaining); w.Write((byte)x.Phase); w.Write((byte)x.Spike); }
                w.Write(Events.Count);
                foreach (var x in Events) { w.Write(x.Time); w.Write((byte)x.Type); w.Write(x.Player); }
                w.Write(StatusEffects.Count);
                foreach (var x in StatusEffects)
                {
                    w.Write(x.Time); w.Write(x.Duration); w.Write(x.Player); w.Write(x.SourcePlayer); w.Write((byte)x.Type); w.Write(x.Source); Str(w, x.Name);
                }
                w.Write(SpikeCarriers.Count);
                foreach (var x in SpikeCarriers) { w.Write(x.Time); w.Write(x.Holder); }
                w.Write(DefuseAttempts.Count);
                foreach (var x in DefuseAttempts)
                {
                    w.Write(x.StartTime); w.Write(x.EndTime); w.Write(x.Player); w.Write(x.ProgressStart); w.Write(x.ProgressEnd); w.Write(x.Defused);
                }
                w.Write(AbilityCasts.Count);
                foreach (var x in AbilityCasts) { w.Write(x.Time); w.Write(x.Player); Str(w, x.Slot); w.Write(x.Round); w.Write(x.X); w.Write(x.Y); w.Write(x.Z); }
                w.Write(AbilityCharges.Count);
                foreach (var x in AbilityCharges)
                {
                    w.Write(x.Time); w.Write(x.Player); Str(w, x.Slot); Str(w, x.Ability); w.Write(x.Charges); w.Write(x.TemporaryCharges); w.Write(x.CooldownEnd);
                }
                w.Write(PlayerStats.Count);
                foreach (var x in PlayerStats)
                {
                    w.Write(x.Time); w.Write(x.Player); w.Write(x.Credits); w.Write(x.RoundStartCredits);
                    w.Write(x.Kills); w.Write(x.Deaths); w.Write(x.Assists); w.Write(x.Score); w.Write(x.UltPoints);
                }
                var hs = Highlights ?? new HighlightSettings();
                w.Write(hs.Enabled); w.Write((byte)Math.Max(0, Math.Min(255, hs.ClutchMin))); w.Write(hs.ClutchRequireWin); w.Write(hs.Ace);
                w.Write((byte)Math.Max(0, Math.Min(255, hs.MultiKill))); w.Write(hs.MultiKillWindow); w.Write(hs.FirstBlood);
                w.Write(Tags.Count);
                foreach (var x in Tags)
                {
                    w.Write(x.Time); w.Write(x.Duration); w.Write(x.Round); w.Write(x.Player); Str(w, x.Label); Str(w, x.Note);
                    int ncat = Math.Min(255, x.Categories.Count);
                    w.Write((byte)ncat);
                    for (int j = 0; j < ncat; j++) Str(w, x.Categories[j]);
                    w.Write((byte)x.Source); Str(w, x.Kind); Str(w, x.Anchor);
                }
                foreach (var f in Frames)
                {
                    w.Write(f.X); w.Write(f.Y); w.Write(f.Z); w.Write(f.YawCenti); w.Write(f.PitchCenti); w.Write((byte)f.Flags);
                    w.Write(f.Health); w.Write(f.Armor); w.Write(f.Shield); w.Write(f.Weapon); w.Write(f.Ammo); w.Write(f.ReserveAmmo);
                }
            }
        }

        private static string Str(BinaryReader r)
        {
            int n = r.ReadUInt16();
            return n == 0 ? "" : Encoding.UTF8.GetString(r.ReadBytes(n));
        }

        private static void Str(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }
    }
}
