using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using ValoReplay.Knowledge;
using ValoReplay.Maps;
using SVector3 = System.Numerics.Vector3;

namespace ValoReplay.EditorTools.AI
{
    /// <summary>Director constants a self-play batch overrides (null = the director's own: with a TempoModel the execute
    /// is drawn per round). For a sensitivity check only: TeamDirector's constants are tuned upstream (vrpsim calibrate)
    /// against real rounds, not against self-play.</summary>
    public sealed class DirectorTweaks
    {
        public float? ExecuteAt;
        public int? StackThreshold;
        public AttackPlan Plan = AttackPlan.Default;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Plan != AttackPlan.Default) parts.Add($"plan {Plan}");
            if (ExecuteAt != null) parts.Add($"execute fixed at {ExecuteAt:0} s left");
            if (StackThreshold != null) parts.Add($"stack threshold {StackThreshold}");
            return parts.Count == 0 ? "defaults" : "override: " + string.Join(", ", parts);
        }
    }

    /// <summary>
    /// All-AI rounds on one map: 5 attackers (team 0) from the attacker spawn against 5 defenders from theirs, every
    /// agent <see cref="AgentMode.Ai"/>, rifles, the five Profile presets per team with the execution and learned
    /// behaviour of a <see cref="SelfPlaySetup"/> (what round play runs), line of sight from Unity raycasts against the
    /// map colliders. Collects what the round result does not say: when the attack executes, how long it
    /// waits at its staging point, rotations, and agents that stand still while wanting to move (stuck).
    /// </summary>
    public static class SelfPlay
    {
        /// <summary>Decision presets (style, aggression, cohesion); SelfPlaySetup gives them measured execution.</summary>
        public static readonly Profile[] Presets = { Profile.Entry, Profile.Support, Profile.Lurker, Profile.Anchor, Profile.Default };

        public sealed class Round
        {
            public int Seed;
            public RoundResult Result;
            public string Site = "", FinalSite = "", FirstKillPlace = "";
            public float PlannedExecute = -1f;   // t the director's ExecuteAt comes due (-1 = never, e.g. Rush)
            public float ExecuteTime = -1f, StagingTime = -1f, PlantTime = -1f, FirstKillTime = -1f;
            public bool FirstKillByAttackers;
            public int AttackerRotations, DefenderRotations;
            public float RotateTime = -1f;
            public int KillsBeforeExecuteAtk, KillsBeforeExecuteDef;   // kills made by attackers / by defenders
            public int AttackerKills, DefenderKills;
            public int StuckEpisodes;
        }

        public sealed class Stuck
        {
            public string Cause = "", Place = "";
            public AgentAction Action;
            public bool Attacker;
        }

        public sealed class Batch
        {
            public string Map = "";
            public DirectorTweaks Tweaks = new DirectorTweaks();
            public SelfPlaySetup Setup;
            public readonly List<Round> Rounds = new List<Round>();
            public readonly List<Stuck> Stuck = new List<Stuck>();
            public double Seconds;
            public bool Cancelled;
            public readonly Dictionary<string, (SVector3 staging, float spawnToStaging, float stagingToSite, float defSpawnToStaging, float defSiteToStaging)> Staging =
                new Dictionary<string, (SVector3, float, float, float, float)>();

            public float AttackerWinRate => Rounds.Count == 0 ? 0f : Rounds.Count(r => r.Result.AttackersWon) / (float)Rounds.Count;
        }

        public static SimOptions Options()
        {
            var ks = new KnowledgeSettings();
            return new SimOptions
            {
                FovHalfAngle = ks.fieldOfView * 0.5f, MaxSightRange = ks.sightRange,
                FootstepRange = ks.footstepRange, GunshotRange = ks.gunshotRange,
            };
        }

        /// <summary>Plays <paramref name="rounds"/> rounds (seeds seed0..) on map with setup's learned behaviour (replacing
        /// the map's own) and profiles. progress(i, n) returns false to stop.</summary>
        public static Batch Play(SimMap map, SelfPlaySetup setup, Func<Collider, bool> isMap, int rounds, int seed0 = 0,
            DirectorTweaks tweaks = null, Func<int, int, bool> progress = null)
        {
            tweaks ??= new DirectorTweaks();
            map = setup.Apply(map);
            var batch = new Batch { Map = map.Map, Tweaks = tweaks, Setup = setup };
            var sight = new MapSightTest(isMap);
            var options = Options();
            var info = MapCatalog.Find(map.Map);
            var spawnsAtk = Slots(map.Grid, map.AttackerSpawn);
            var spawnsDef = Slots(map.Grid, map.DefenderSpawn);
            foreach (var s in map.Sites) batch.Staging[s.Name] = StagingOf(map, s);
            var sw = Stopwatch.StartNew();
            var path = new List<int>();

            for (int i = 0; i < rounds; i++)
            {
                if (progress != null && !progress(i, rounds)) { batch.Cancelled = true; break; }
                var setups = new List<AgentSetup>();
                for (int team = 0; team < 2; team++)
                {
                    var slots = team == 0 ? spawnsAtk : spawnsDef;
                    var facing = team == 0 ? map.DefenderSpawn : map.AttackerSpawn;
                    var profiles = team == 0 ? setup.Attackers : setup.Defenders;
                    for (int k = 0; k < 5; k++)
                    {
                        var p = slots[k % slots.Count];
                        var profile = profiles[k % profiles.Length];
                        setups.Add(new AgentSetup
                        {
                            Team = team, Attacking = team == 0, Position = p, Yaw = SimSpace.YawOf(facing - p),
                            Profile = profile, Mode = AgentMode.Ai, Name = $"{(team == 0 ? "atk" : "def")}{k} {profile.Style}",
                        });
                    }
                }
                var sim = new RoundSim(map, setups, new RoundSetup { Remaining = options.RoundTime, Plan = tweaks.Plan }, options, seed0 + i);
                sim.SightTest = (a, b) => sight.Clear(U(a), U(b));
                var atkDir = sim.Director(0);
                if (tweaks.ExecuteAt != null)
                {
                    atkDir.ExecuteAt = tweaks.ExecuteAt.Value;
                    atkDir.Executing = sim.Remaining <= atkDir.ExecuteAt;   // a drawn tempo may already have executed at t 0
                }
                if (tweaks.StackThreshold != null) atkDir.StackThreshold = tweaks.StackThreshold.Value;

                var r = new Round { Seed = seed0 + i, Site = atkDir.TargetSite };
                if (!float.IsInfinity(atkDir.ExecuteAt)) r.PlannedExecute = Math.Max(0f, options.RoundTime - atkDir.ExecuteAt);
                batch.Staging.TryGetValue(r.Site, out var stage);
                var last = new SVector3[sim.Agents.Count];
                var still = new int[sim.Agents.Count];
                for (int k = 0; k < last.Length; k++) last[k] = sim.Agents[k].Position;
                int kills = 0, tick = 0;

                while (!sim.Over)
                {
                    sim.Step();
                    tick++;
                    if (r.ExecuteTime < 0f && atkDir.Executing) r.ExecuteTime = sim.Time;
                    if (r.RotateTime < 0f && atkDir.Rotations > 0) r.RotateTime = sim.Time;
                    for (; kills < sim.Kills.Count; kills++)
                    {
                        var k = sim.Kills[kills];
                        bool byAtk = k.KillerTeam == 0 && k.Killer >= 0;
                        if (r.FirstKillTime < 0f) { r.FirstKillTime = k.Time; r.FirstKillByAttackers = byAtk; r.FirstKillPlace = Place(info, sim.Agents[k.Victim].Position); }
                        if (r.ExecuteTime < 0f && k.Killer >= 0) { if (byAtk) r.KillsBeforeExecuteAtk++; else r.KillsBeforeExecuteDef++; }
                    }
                    if (r.StagingTime < 0f && stage.staging != default && r.ExecuteTime < 0f)
                    {
                        int alive = 0, there = 0;
                        foreach (var a in sim.Agents)
                        {
                            if (!a.Alive || !a.Attacking) continue;
                            alive++;
                            if (SimSpace.FlatDistance(a.Position, stage.staging) <= 8f) there++;
                        }
                        if (alive > 0 && there * 2 >= alive) r.StagingTime = sim.Time;
                    }
                    if (tick % (int)options.TickRate != 0) continue;

                    // stuck: wants to move somewhere more than 2 m away, sees nobody, and has not moved for 3 s
                    foreach (var a in sim.Agents)
                    {
                        int ix = a.Index;
                        bool wantsToMove = a.Alive && a.Mode == AgentMode.Ai && (a.Action == AgentAction.Advance || a.Action == AgentAction.Peek
                            || a.Action == AgentAction.Trade || a.Action == AgentAction.FallBack) && SimSpace.FlatDistance(a.Position, a.Goal) > 2f;
                        if (wantsToMove) for (int j = 0; j < sim.Agents.Count; j++) if (sim.Sees(ix, j)) { wantsToMove = false; break; }
                        bool moved = SimSpace.FlatDistance(a.Position, last[ix]) > 0.3f;
                        last[ix] = a.Position;
                        still[ix] = wantsToMove && !moved ? still[ix] + 1 : 0;
                        if (still[ix] != 3) continue;
                        int from = map.Grid.Nearest(a.Position), to = map.Grid.Nearest(a.Goal, 6f);
                        string cause = from < 0 ? "agent off the grid" : to < 0 ? "goal off the grid (>6 m)"
                            : !map.Grid.FindPath(from, to, path) ? "no path"
                            : from == to || SimSpace.FlatDistance(map.Grid.Position(to), a.Goal) > 2f ? "at the node nearest an off-grid goal"
                            : "path exists, no replan";   // RoundSim.MoveTowards replans a finished path only when Time >= NextDecision - 0.01, which Decide has just moved on
                        batch.Stuck.Add(new Stuck { Cause = cause, Place = Place(info, a.Position), Action = a.Action, Attacker = a.Attacking });
                        r.StuckEpisodes++;
                    }
                }
                r.Result = sim.RunToEnd();
                r.FinalSite = atkDir.TargetSite;
                r.AttackerRotations = atkDir.Rotations;
                r.DefenderRotations = sim.Director(1).Rotations;
                var planted = sim.Events.Find(e => e.Type == SimEventType.Planted);
                if (planted.Type == SimEventType.Planted) r.PlantTime = planted.Time;
                foreach (var k in sim.Kills) { if (k.Killer < 0) continue; if (k.KillerTeam == 0) r.AttackerKills++; else r.DefenderKills++; }
                batch.Rounds.Add(r);
            }
            batch.Seconds = sw.Elapsed.TotalSeconds;
            return batch;
        }

        /// <summary>Five start spots around a spawn, ~2 m apart, on the grid.</summary>
        static List<SVector3> Slots(NavGrid g, SVector3 spawn)
        {
            var list = new List<SVector3>();
            for (int k = 0; k < 5; k++)
            {
                double ang = k * Math.PI * 2 / 5;
                var p = spawn + new SVector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang)) * 2f;
                int n = g.Nearest(p, 3f);
                list.Add(n >= 0 ? g.Position(n) : spawn);
            }
            return list;
        }

        /// <summary>The attackers' staging point for a site as TeamDirector computes it (55% of the walking path from the
        /// attacker spawn), and path lengths around it.</summary>
        static (SVector3, float, float, float, float) StagingOf(SimMap map, SimMap.Site site)
        {
            const float fraction = 0.55f;   // TeamDirector.Objective: Along(..., AttackerSpawn, site, 0.55)
            var g = map.Grid;
            int a = g.Nearest(map.AttackerSpawn, 8f), b = g.Nearest(site.Center, 8f);
            var path = new List<int>();
            var p = SVector3.Lerp(map.AttackerSpawn, site.Center, fraction);
            float total = 0f;
            if (a >= 0 && b >= 0 && g.FindPath(a, b, path) && path.Count > 0)
            {
                var prev = g.Position(a);
                foreach (int n in path) { total += SVector3.Distance(prev, g.Position(n)); prev = g.Position(n); }
                float want = total * fraction, run = 0f;
                prev = g.Position(a);
                foreach (int n in path)
                {
                    run += SVector3.Distance(prev, g.Position(n));
                    prev = g.Position(n);
                    if (run >= want) { p = prev; break; }
                }
            }
            int s = g.Nearest(p, 8f);
            float toSite = s >= 0 && b >= 0 ? g.PathLength(s, b) : float.NaN;
            int d = g.Nearest(map.DefenderSpawn, 8f);
            float defToStage = s >= 0 && d >= 0 ? g.PathLength(d, s) : float.NaN;
            float siteToStage = s >= 0 && b >= 0 ? g.PathLength(b, s) : float.NaN;
            return (p, total * fraction, toSite, defToStage, siteToStage);
        }

        public static string Place(MapInfo info, SVector3 p)
        {
            if (info == null || info.Callouts.Length == 0) return $"({p.X:0},{p.Y:0},{p.Z:0})";
            var c = info.NearestCallout(p.Z * 100f, p.X * 100f);
            return c?.Name ?? "?";
        }

        static Vector3 U(SVector3 v) => new Vector3(v.X, v.Y, v.Z);

        // ------------------------------------------------------------------ report

        public static string Summary(Batch b)
        {
            var sb = new StringBuilder();
            var rs = b.Rounds;
            int n = rs.Count;
            if (n == 0) return "no rounds played";
            string Pct(int k, int of) => of == 0 ? "-" : $"{100f * k / of:0.0}% ({k}/{of})";
            float Mean(IEnumerable<float> v) { var l = v.ToList(); return l.Count == 0 ? float.NaN : l.Average(); }
            string Dist(IEnumerable<float> v)
            {
                var l = v.OrderBy(x => x).ToList();
                if (l.Count == 0) return "-";
                return $"mean {l.Average():0.0}, p10 {l[(int)(0.1 * (l.Count - 1))]:0.0}, median {l[l.Count / 2]:0.0}, p90 {l[(int)(0.9 * (l.Count - 1))]:0.0}";
            }

            int wins = rs.Count(r => r.Result.AttackersWon);
            double p = wins / (double)n, ci = 1.96 * Math.Sqrt(p * (1 - p) / n) * 100;
            sb.AppendLine($"Self-play on {MapCatalog.DisplayName(b.Map)}: {n} rounds{(b.Cancelled ? " (cancelled)" : "")}, director {b.Tweaks}; {b.Seconds:0.0} s wall ({1000 * b.Seconds / n:0} ms/round)");
            if (b.Setup != null)
                sb.AppendLine($"  AI                  tempo {(b.Tweaks.ExecuteAt != null ? "overridden" : b.Setup.TempoLabel)}, profiles {b.Setup.ProfileLabel}, looks {(b.Setup.Look != null ? "learned" : "none")}");
            sb.AppendLine($"  attacker win rate   {Pct(wins, n)}  ±{ci:0.0}");
            int plants = rs.Count(r => r.Result.Planted);
            sb.AppendLine($"  plant rate          {Pct(plants, n)}; attackers win {Pct(rs.Count(r => r.Result.Planted && r.Result.AttackersWon), plants)} after a plant, {Pct(rs.Count(r => !r.Result.Planted && r.Result.AttackersWon), n - plants)} without");
            sb.AppendLine($"  round duration (s)  {Dist(rs.Select(r => r.Result.Duration))}");
            sb.AppendLine($"  plant time (s)      {Dist(rs.Where(r => r.PlantTime >= 0f).Select(r => r.PlantTime))}");
            sb.Append("  end reasons         ");
            sb.AppendLine(string.Join(", ", rs.GroupBy(r => (r.Result.Reason, r.Result.AttackersWon)).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key.Reason} → {(g.Key.AttackersWon ? "atk" : "def")} {g.Count()}")));
            sb.AppendLine($"  kills per round     {Mean(rs.Select(r => (float)r.Result.Kills.Count)):0.00} (by attackers {Mean(rs.Select(r => (float)r.AttackerKills)):0.00}, by defenders {Mean(rs.Select(r => (float)r.DefenderKills)):0.00}); rounds with no kill {rs.Count(r => r.Result.Kills.Count == 0)}");
            var fk = rs.Where(r => r.FirstKillTime >= 0f).ToList();
            sb.AppendLine($"  first kill (s)      {Dist(fk.Select(r => r.FirstKillTime))}; by attackers {Pct(fk.Count(r => r.FirstKillByAttackers), fk.Count)}; first-kill team wins {Pct(fk.Count(r => r.FirstKillByAttackers == r.Result.AttackersWon), fk.Count)}");
            sb.AppendLine($"  first kill at       {string.Join(", ", fk.GroupBy(r => r.FirstKillPlace).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} {g.Count()}"))}");
            foreach (var g in rs.GroupBy(r => r.Site).OrderBy(g => g.Key))
                sb.AppendLine($"  site {g.Key,-2}             targeted {g.Count()}x, attackers win {Pct(g.Count(r => r.Result.AttackersWon), g.Count())}, plant {Pct(g.Count(r => r.Result.Planted), g.Count())}");

            sb.AppendLine("  director (attackers)");
            var planned = rs.Where(r => r.PlannedExecute >= 0f).ToList();
            sb.AppendLine($"    execute due       {Pct(planned.Count, n)} at t {Dist(planned.Select(r => r.PlannedExecute))} (ExecuteAt as set at the start; fixed default = t 45)");
            var ex = rs.Where(r => r.ExecuteTime >= 0f).ToList();
            sb.AppendLine($"    executed          {Pct(ex.Count, n)} at t {Dist(ex.Select(r => r.ExecuteTime))}");
            var st = rs.Where(r => r.StagingTime >= 0f).ToList();
            sb.AppendLine($"    reached staging   {Pct(st.Count, n)} (half the living attackers within 8 m) at t {Dist(st.Select(r => r.StagingTime))}");
            var waited = rs.Where(r => r.StagingTime >= 0f && r.ExecuteTime >= 0f).Select(r => r.ExecuteTime - r.StagingTime).ToList();
            sb.AppendLine($"    wait at staging   {Dist(waited)} s");
            sb.AppendLine($"    first kill before execute {Pct(rs.Count(r => r.FirstKillTime >= 0f && (r.ExecuteTime < 0f || r.FirstKillTime < r.ExecuteTime)), n)}; kills before execute: by attackers {Mean(rs.Select(r => (float)r.KillsBeforeExecuteAtk)):0.00}, by defenders {Mean(rs.Select(r => (float)r.KillsBeforeExecuteDef)):0.00} per round");
            var rot = rs.Where(r => r.AttackerRotations > 0).ToList();
            sb.AppendLine($"    rotated (stack)   {Pct(rot.Count, n)} at t {Dist(rot.Select(r => r.RotateTime))}; win {Pct(rot.Count(r => r.Result.AttackersWon), rot.Count)} vs {Pct(rs.Count(r => r.AttackerRotations == 0 && r.Result.AttackersWon), n - rot.Count)} without");
            sb.AppendLine($"    defenders rotated {Pct(rs.Count(r => r.DefenderRotations > 0), n)}");
            foreach (var kv in b.Staging.OrderBy(k => k.Key))
                sb.AppendLine($"    staging for {kv.Key}     {kv.Value.spawnToStaging:0} m from attacker spawn, {kv.Value.stagingToSite:0} m to the site; defenders reach it in {kv.Value.defSpawnToStaging:0} m from spawn, {kv.Value.defSiteToStaging:0} m from the site");

            int stuckRounds = rs.Count(r => r.StuckEpisodes > 0);
            sb.AppendLine($"  stuck               {b.Stuck.Count} episodes (≥3 s still while moving to a goal >2 m away, nobody in sight) in {Pct(stuckRounds, n)} of rounds");
            foreach (var g in b.Stuck.GroupBy(s => s.Cause).OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Key,-24} {g.Count(),4}  ({string.Join(", ", g.GroupBy(s => s.Place).OrderByDescending(x => x.Count()).Take(5).Select(x => $"{x.Key} {x.Count()}"))}; actions {string.Join(", ", g.GroupBy(s => s.Action).Select(x => $"{x.Key} {x.Count()}"))})");
            return sb.ToString();
        }
    }
}
