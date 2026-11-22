// vrpsim - the simulation on real .vrp files.
//   vrpsim map <a.vrp> [more.vrp ...] [--pgm out.pgm]       NavGrid coverage, sites and spawns built from the replays
//   vrpsim profiles <match.vrp>                                fitted profile and raw stats per player
//   vrpsim run <match.vrp> --at <round>[:<m:ss>] [--map more.vrp ...] [-n 200] [--ci 0.03] [--seed 0] [--threads 0] [--ai] [--takeover <player>]
//       simulate the rest of a round from a moment of it (default: the barrier drop). By default agents follow the
//       recording until they diverge, so without --takeover or --ai every run should reproduce what happened.
//       --ci h: keep running (up to -n) until the attacker win rate is known to +-h (95%).
//   vrpsim calibrate <a.vrp> [more.vrp ...] [-n 100] [--at 0,20,40] [--profiles loo|fitted|default] [--knobs] [--neutral decisions,sense] [--learned holdout|all|none] [--perception default|valoreplay] [--seed 0] [--threads 0] [--csv out.csv] [--players]
//       restart every recorded round from snapshots (seconds after the barrier) with everyone as AI and score the
//       simulated outcomes against what really happened. See Calibration.cs. --knobs: no measured execution.
//       --neutral: replace fitted decision knobs (aggression, cohesion, style) and/or sense (Skill) with neutral ones.
//       --learned: per-map learned behaviour (LookModel, TempoModel) from the other replays only, all of them, or none.
//       --perception valoreplay: ValoReplay round play's view and hearing ranges, with the transfer tuned for them.
//   vrpsim tune <a.vrp> [more.vrp ...] [-n 40] [--iterations 3] [--learned holdout|all|none] [--perception default|valoreplay] [--seed 0] [--threads 0]
//       measure the population's shooting and find the constants that turn it into simulation parameters
//       (ProfileTuner.cs); prints them ready to paste into ProfileFitter.
//   vrpsim bots <a.vrp> [more.vrp ...] [-n 2] [--only roster,ladder,styles,brains] [--seed 0] [--threads 0]
//       example bots (Bots.cs: skill tiers x archetypes, five-player lineups) playing each other from every recorded
//       round's barrier drop, each round with both lineups attacking (BotMatch.cs). -n: runs per round and side.
//   vrpsim duels <a.vrp> [more.vrp ...] [-n 2] [--players] [--threads 0]
//       who sees whom first, by how much and what it is worth, time to the first shot, first-shot miss and overswing by
//       flick, exposure and walls: recorded rounds against simulated ones measured the same way (Duels.cs). --players:
//       how exposure and walls correlate with skill per recorded player-match.
//   calibrate, bots and duels take --set <SimOptions field>=<value> (repeatable), e.g. --set PreAimReaction=0.6, to try constants.
//   vrpsim plans <a.vrp> [more.vrp ...] [-n 2] [--attack default,rush,slow,fake,latecall] [--seed 0] [--threads 0]
//       every recorded round from the barrier drop, all AI with fitted profiles, played with each attack plan on the same
//       seeds: win, plant and rotation rates per plan, and each plan's win rate against Default's round for round.
//   run, calibrate, bots, duels, plans and scorecard take --set <SimOptions field>=<value> (repeatable); run, calibrate and scorecard take
//   --attack <plan or mix>, e.g. --attack fake or --attack default=3,fake=1,latecall=1 (Plans.AttackMix).
//   vrpsim scorecard <folder|a.vrp> [...] [-n 40] [--seeds 0-4] [--out card.tsv] [--compare baseline.tsv] [--markdown summary.md] [--tolerance 4.5] [--threads 0]
//       calibrate (default settings) on each seed and boil it down to ~50 named metrics, each with which way is better,
//       their mean and spread between seeds (Scorecard.cs). --out saves it; --compare checks it against a saved one and
//       exits 1 when a metric got worse by more than --tolerance standard errors (4.5), or the two are not comparable.
//       The baseline is sim/scorecard.tsv: vrpsim scorecard data/replays -n 40 --seeds 0-15 --out sim/scorecard.tsv
//       (regenerate it in the same commit as a change meant to move it); CI compares two seeds against it.
//   vrpsim compare <baseline.tsv> <current.tsv> [--markdown summary.md] [--tolerance 4.5]
//       the same check between two saved scorecards.
//   vrpsim validate <folder|a.vrp> [...]
//       checks replays meant for publishing: each loads with this .vrp layout, is anonymized (pseudonymous ids, generated
//       or no names, no crosshair names, no recording time, no user tags), is named <Map>-<match id>.vrp, and plays a
//       short all-AI round. Exit code 1 if any fails.
using System.Globalization;
using System.Text.RegularExpressions;
using System.Numerics;
using ValorantReplay.Playback;
using ValorantReplay.Sim;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) return Usage();
            return args[0] switch
            {
                "map" => MapCmd(args.Skip(1).ToList()),
                "profiles" => ProfilesCmd(args[1]),
                "run" => RunCmd(args.Skip(1).ToList()),
                "calibrate" => CalibrateCmd(args.Skip(1).ToList()),
                "tune" => TuneCmd(args.Skip(1).ToList()),
                "validate" => ValidateCmd(args.Skip(1).ToList()),
                "bots" => BotsCmd(args.Skip(1).ToList()),
                "duels" => DuelsCmd(args.Skip(1).ToList()),
                "plans" => PlansCmd(args.Skip(1).ToList()),
                "scorecard" => ScorecardCmd(args.Skip(1).ToList()),
                "compare" => CompareCmd(args.Skip(1).ToList()),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is ArgumentException || e is IOException || e is InvalidDataException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("vrpsim map <a.vrp> [more.vrp ...] [--pgm out.pgm]");
        Console.Error.WriteLine("vrpsim profiles <match.vrp>");
        Console.Error.WriteLine("vrpsim run <match.vrp> --at <round>[:<m:ss>] [--map more.vrp ...] [-n 200] [--ci 0.03] [--seed 0] [--threads 0] [--ai] [--takeover <player>]");
        Console.Error.WriteLine("vrpsim calibrate <a.vrp> [more.vrp ...] [-n 100] [--at 0,20,40] [--profiles loo|fitted|default] [--knobs] [--neutral decisions,sense] [--learned holdout|all|none] [--perception default|valoreplay] [--seed 0] [--threads 0] [--csv out.csv] [--players]");
        Console.Error.WriteLine("vrpsim tune <a.vrp> [more.vrp ...] [-n 40] [--iterations 3] [--learned holdout|all|none] [--perception default|valoreplay] [--seed 0] [--threads 0]");
        Console.Error.WriteLine("vrpsim validate <folder|a.vrp> [...]");
        Console.Error.WriteLine("vrpsim duels <a.vrp> [more.vrp ...] [-n 2] [--players] [--threads 0]");
        Console.Error.WriteLine("vrpsim bots <a.vrp> [more.vrp ...] [-n 2] [--only roster,ladder,styles,brains] [--seed 0] [--threads 0]");
        Console.Error.WriteLine("vrpsim plans <a.vrp> [more.vrp ...] [-n 2] [--attack default,rush,slow,fake,latecall] [--seed 0] [--threads 0]");
        Console.Error.WriteLine("vrpsim scorecard <folder|a.vrp> [...] [-n 40] [--seeds 0-4] [--out card.tsv] [--compare baseline.tsv] [--markdown summary.md] [--tolerance 4.5] [--threads 0]");
        Console.Error.WriteLine("vrpsim compare <baseline.tsv> <current.tsv> [--markdown summary.md] [--tolerance 4.5]");
        return 2;
    }

    // ---------------------------------------------------------------- validate

    static readonly Regex Pseudonym = new Regex("^[0-9a-f]{24}$");

    static int ValidateCmd(List<string> a)
    {
        var files = new List<string>();
        foreach (var p in a)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*.vrp").OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(p)) files.Add(p);
            else { Console.Error.WriteLine($"{p}: not found"); return 1; }
        }
        // a .vrf or a key must never sit next to published replays
        foreach (var p in a.Where(Directory.Exists))
            foreach (var f in Directory.GetFiles(p).Where(f => f.EndsWith(".vrf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".profile", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".key", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"FAIL {Path.GetFileName(f)}: raw replays and keys don't belong here");
                files.Add(null);
            }
        int failed = files.Count(f => f == null);
        foreach (var f in files.Where(f => f != null))
        {
            var problems = ValidateOne(f);
            Console.WriteLine(problems.Count == 0 ? $"ok   {Path.GetFileName(f)}" : $"FAIL {Path.GetFileName(f)}: {string.Join("; ", problems)}");
            if (problems.Count > 0) failed++;
        }
        Console.WriteLine($"{files.Count(f => f != null)} replays checked, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static List<string> ValidateOne(string path)
    {
        var problems = new List<string>();
        VrpReplay r;
        try { r = VrpReplay.Load(path); }
        catch (Exception e) { problems.Add($"does not load ({e.Message})"); return problems; }

        if (!Pseudonym.IsMatch(r.MatchId)) problems.Add("match id is not a pseudonym");
        if (r.RecordedAtTicks != 0) problems.Add("recording time is set");
        for (int i = 0; i < r.Players.Count; i++)
        {
            var p = r.Players[i];
            if (p.Puuid.Length > 0 && !Pseudonym.IsMatch(p.Puuid)) problems.Add($"player {i}: id is not a pseudonym");
            if (p.Name.Length > 0 && p.Name != VrpReplay.GeneratedName(p.Puuid)) problems.Add($"player {i}: name is not the generated one");
            if (p.CrosshairProfile.Length > 0) problems.Add($"player {i}: crosshair profile name is set");
        }
        if (r.Tags.Any(t => t.Source == TagSource.User)) problems.Add("has user tags");
        string expected = (MapName(r) + "-" + r.MatchId).Replace(' ', '_') + ".vrp";
        if (!string.Equals(Path.GetFileName(path), expected, StringComparison.Ordinal)) problems.Add($"should be named {expected}");
        if (r.Space != CoordinateSpace.Unity) problems.Add("not in Unity coordinates");

        // a short all-AI round from the first barrier drop: the replay is usable by the simulation
        try
        {
            var round = r.Rounds.FirstOrDefault(x => x.BarrierTime >= 0f && x.AttackingTeam != 255);
            if (round == null) problems.Add("no playable round");
            else
            {
                var map = SimMap.FromReplays(new[] { r });
                var sim = RoundSim.FromReplay(r, map, round.BarrierTime, _ => Profile.Default, followRecording: false, seed: 1);
                for (int i = 0; i < 400 && !sim.Over; i++) sim.Step();
            }
        }
        catch (Exception e) { problems.Add($"simulation failed ({e.GetType().Name}: {e.Message})"); }
        return problems;
    }

    static string Take(List<string> a, string flag)
    {
        int i = a.IndexOf(flag);
        if (i < 0) return null;
        if (i + 1 >= a.Count) throw new ArgumentException($"{flag} needs a value");
        string v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    static bool Flag(List<string> a, string flag) => a.Remove(flag);

    /// <summary>Applies every --set Field=value to a SimOptions (a new one when null and something is set).</summary>
    static SimOptions Sets(List<string> a, SimOptions o)
    {
        for (string kv; (kv = Take(a, "--set")) != null;)
        {
            var parts = kv.Split('=', 2);
            var field = typeof(SimOptions).GetField(parts[0]) ?? throw new ArgumentException($"--set {parts[0]}: no such SimOptions field");
            if (parts.Length < 2) throw new ArgumentException($"--set {kv}: expected Field=value");
            o ??= new SimOptions();
            field.SetValue(o, Convert.ChangeType(parts[1], field.FieldType, CultureInfo.InvariantCulture));
            Console.WriteLine($"SimOptions.{parts[0]} = {parts[1]}");
        }
        return o;
    }

    /// <summary>Applies --attack <plan or mix> (Plans.AttackMix) to a SimOptions (a new one when null and it is given).</summary>
    static SimOptions Attacks(List<string> a, SimOptions o)
    {
        string spec = Take(a, "--attack");
        if (spec == null) return o;
        o ??= new SimOptions();
        o.AttackPlans = Plans.AttackMix(spec);
        Console.WriteLine("attack plans: " + string.Join(", ", o.AttackPlans.Select(p => $"{p.Plan.Name} {p.Weight:0.##}")));
        return o;
    }

    static string MapName(VrpReplay r) => MapCatalog.DisplayName(r.Map);

    // ---------------------------------------------------------------- map

    static int MapCmd(List<string> a)
    {
        string pgm = Take(a, "--pgm");
        var replays = a.Select(VrpReplay.Load).ToList();
        var map = SimMap.FromReplays(replays);
        var g = map.Grid;
        int filled = g.Nodes.Count(n => n.Samples == 0);
        int columns = g.Nodes.Select(n => (n.X, n.Z)).Distinct().Count();
        Console.WriteLine($"{MapName(replays[0])} ({map.Map}) from {map.Replays} replays, {replays.Sum(r => r.Rounds.Count)} rounds");
        Console.WriteLine($"  grid {g.Width}x{g.Depth} cells of {g.CellSize} m = {g.Width * g.CellSize:0} x {g.Depth * g.CellSize:0} m");
        Console.WriteLine($"  walkable nodes {g.Count} in {columns} columns ({g.Count - columns} on upper floors), {filled} filled holes");
        foreach (var s in map.Sites) Console.WriteLine($"  site {s.Name}: centre {V(s.Center)}, radius {s.Radius:0.0} m, from {s.Plants} plants");
        Console.WriteLine($"  attacker spawn {V(map.AttackerSpawn)}, defender spawn {V(map.DefenderSpawn)}");
        int a0 = g.Nearest(map.AttackerSpawn, 10f);
        foreach (var s in map.Sites)
        {
            int b = g.Nearest(s.Center, 10f);
            float walk = a0 >= 0 && b >= 0 ? g.PathLength(a0, b) : float.PositiveInfinity;
            Console.WriteLine($"  walk attacker spawn -> {s.Name}: {(float.IsFinite(walk) ? $"{walk:0} m ({walk / 5.4f:0.0} s running)" : "no path")}");
        }
        if (pgm != null)
        {
            WritePgm(g, pgm);
            Console.WriteLine($"  coverage image: {pgm} (white = many samples, grey = few, black = never walked)");
        }
        return 0;
    }

    // top-down coverage, log-scaled sample counts; north (+Z) up
    static void WritePgm(NavGrid g, string path)
    {
        var px = new byte[g.Width * g.Depth];
        foreach (var n in g.Nodes)
        {
            int i = (g.Depth - 1 - (n.Z - g.MinZ)) * g.Width + (n.X - g.MinX);
            byte v = n.Samples == 0 ? (byte)60 : (byte)Math.Min(255, 80 + 35 * Math.Log(1 + n.Samples));
            px[i] = Math.Max(px[i], v);
        }
        using var f = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{g.Width} {g.Depth}\n255\n");
        f.Write(header);
        f.Write(px);
    }

    // ---------------------------------------------------------------- profiles

    static int ProfilesCmd(string path)
    {
        var r = VrpReplay.Load(path);
        Console.WriteLine($"{MapName(r)}, {r.Rounds.Count} rounds");
        Console.WriteLine($"{"#",2} {"team",4} {"player",-20} {"agent",-10} {"style",-8} {"skill",5} {"aggr",5} {"coh",5} {"acc",5} {"hs",4} {"react",5} {"look",5} | " +
            $"{"K",3} {"D",3} {"HS",4} {"open",5} {"1stD",5} {"trd",4} {"mate m",6} | {"bullets",7} {"acc",4} {"hs/hit",6} {"react",5} {"n",3} {"aim err",7}");
        foreach (var f in ProfileFitter.Fit(r))
        {
            var p = f.Profile;
            var t = f.Tally;
            Console.WriteLine($"{f.Player,2} {r.Players[f.Player].Team,4} {Trunc(f.Name, 20),-20} {Trunc(f.Agent, 10),-10} {p.Style,-8} {p.Skill,5:0.00} {p.Aggression,5:0.00} {p.Cohesion,5:0.00} " +
                $"{p.BaseAccuracy,5:0.00} {p.HeadshotShare,4:0.00} {p.ReactionTime,5:0.00} {p.LookSpread,5:0.0} | " +
                $"{t.Kills,3} {t.Deaths,3} {f.HeadshotRate,4:P0} {t.OpeningRate,5:P0} {t.FirstDeathRate,5:P0} {t.TradedRate,4:P0} {f.TeammateDistance,6:0.0} | " +
                $"{t.Bullets,7} {t.Accuracy,4:P0} {t.HeadHitRate,6:P0} {t.ReactionMean,5:0.00} {t.Reactions,3} {t.AimErrorMean,6:0.0}°");
        }
        Console.WriteLine("left: profile (acc/hs/react/look = simulation parameters from the measured stats on the right, shrunk towards the population)");
        return 0;
    }

    // ---------------------------------------------------------------- run

    static int RunCmd(List<string> a)
    {
        string at = Take(a, "--at") ?? throw new ArgumentException("--at <round>[:<m:ss>] is required");
        int n = int.Parse(Take(a, "-n") ?? "200", CultureInfo.InvariantCulture);
        int seed = int.Parse(Take(a, "--seed") ?? "0", CultureInfo.InvariantCulture);
        int threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture);
        string ci = Take(a, "--ci");
        string takeover = Take(a, "--takeover");
        bool ai = Flag(a, "--ai");
        var options = Attacks(a, Sets(a, null));
        var more = new List<string>();
        for (string m; (m = Take(a, "--map")) != null;) more.Add(m);
        if (a.Count != 1) throw new ArgumentException("one match .vrp expected");

        var r = VrpReplay.Load(a[0]);
        var map = SimMap.FromReplays(new[] { r }.Concat(more.Select(VrpReplay.Load)));
        var parts = at.Split(':', 2);
        int round = int.Parse(parts[0], CultureInfo.InvariantCulture);
        if (round < 1 || round > r.Rounds.Count) throw new ArgumentException($"round {round}: the match has {r.Rounds.Count}");
        var rd = r.Rounds[round - 1];
        float start = Math.Max(rd.StartTime, rd.BarrierTime) + (parts.Length > 1 ? Clock(parts[1]) : 0f);
        var fitted = ProfileFitter.Fit(r);

        Console.WriteLine($"{MapName(r)} round {round} from {start - Math.Max(rd.StartTime, rd.BarrierTime):0.0}s after the barrier; map from {map.Replays} replays, sites {string.Join(" ", map.Sites.Select(s => s.Name))}");
        Console.WriteLine($"recorded: team {rd.WinningTeam} won ({rd.EndReason}), {(rd.PlantTime >= 0 ? "planted " + rd.Site : "no plant")}, attackers team {rd.AttackingTeam}");

        RoundSim Make(int i)
        {
            var sim = RoundSim.FromReplay(r, map, start, p => fitted[p].Profile, options, seed: seed + i, followRecording: !ai);
            if (takeover != null) sim.TakeControl(int.Parse(takeover, CultureInfo.InvariantCulture));
            return sim;
        }
        var first = Make(0);
        var rs0 = r.RoundStateAt(start);
        Console.WriteLine($"start: {first.Remaining:0.0}s of round time left (recorded clock {rs0.Remaining:0.0}s, {rs0.Phase}), spike {first.SpikeState}" +
            $"{(first.SpikeCarrier >= 0 ? " carried by " + first.SpikeCarrier : "")}, {first.Agents.Count(x => x.Alive)} alive");
        var b = ci != null
            ? SimBatch.RunUntil(Make, float.Parse(ci, CultureInfo.InvariantCulture), n, threads: threads)
            : SimBatch.Run(Make, n, threads);
        n = b.Rounds;
        int atkTeam = rd.AttackingTeam;
        Console.WriteLine($"simulated {n}: attackers (team {atkTeam}) win {b.AttackerWinRate:P0} +-{b.AttackerWinHalfWidth:P0}, planted {b.Planted / (float)n:P0}, mean {b.MeanDuration:0.0}s, " +
            $"{b.MeanDiverged:0.0} players diverged per run");
        Console.WriteLine("  ends: " + string.Join(", ", b.Reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / (float)n:P0}")));
        Console.WriteLine($"{"#",2} {"player",-20} {"profile",-44} | {"K/round",7} {"D/round",7} {"open",5}");
        foreach (var f in fitted)
        {
            var t = b.Players.TryGetValue(f.Player, out var x) ? x : new PlayerTally();
            Console.WriteLine($"{f.Player,2} {Trunc(f.Name, 20),-20} {f.Profile,-44} | {t.Kills / (float)n,7:0.00} {t.Deaths / (float)n,7:0.00} {t.OpeningRate,5:P0}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- duels

    static int DuelsCmd(List<string> a)
    {
        int n = int.Parse(Take(a, "-n") ?? "2", CultureInfo.InvariantCulture);
        int threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture);
        bool players = Flag(a, "--players");
        var options = Sets(a, null);
        var files = a.SelectMany<string, string>(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.vrp").OrderBy(f => f, StringComparer.Ordinal) : new[] { p }).ToList();
        if (files.Count == 0) throw new ArgumentException("at least one .vrp expected");
        Duels.Run(files.Select(VrpReplay.Load).ToList(), n, options, players, threads);
        return 0;
    }

    // ---------------------------------------------------------------- bots

    static int BotsCmd(List<string> a)
    {
        int n = int.Parse(Take(a, "-n") ?? "2", CultureInfo.InvariantCulture);
        int seed = int.Parse(Take(a, "--seed") ?? "0", CultureInfo.InvariantCulture);
        int threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture);
        var only = (Take(a, "--only") ?? "roster,ladder,styles,brains").Split(',').ToHashSet();
        var simOptions = Sets(a, null);
        var files = a.SelectMany<string, string>(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.vrp").OrderBy(f => f, StringComparer.Ordinal) : new[] { p }).ToList();
        if (files.Count == 0) throw new ArgumentException("at least one .vrp expected");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var arena = new BotArena(files.Select(VrpReplay.Load));
        Console.WriteLine($"{files.Count} replays, {arena.Maps.Count} maps, {arena.Rounds} rounds from the barrier drop x 2 sides x {n} runs = " +
            $"{arena.Rounds * 2 * n:N0} simulated rounds per matchup (recorded positions and guns, every agent AI)");
        BotMatchResult Play(BotLineup x, BotLineup y) => arena.Play(x, y, n, seed, threads, simOptions);

        if (only.Contains("roster"))
        {
            Console.WriteLine();
            Console.WriteLine("tiers         real-game stats the tier is set from              | simulation parameters");
            Console.WriteLine($"  {"tier",-8} {"acc",5} {"hs/hit",6} {"legs",5} {"react",5} {"aim err",7} | {"sense",5} {"acc",5} {"hs",4} {"react",5} {"look",5} {"turn",4} {"memory",6} {"hear",4}");
            foreach (var t in Bots.Tiers)
            {
                var s = Bots.Stats(t);
                var p = Bots.Make(t, BotArchetype.Flex);
                Console.WriteLine($"  {t,-8} {s.Accuracy,5:P0} {s.HeadHits,6:P0} {s.LegHits,5:P0} {s.Reaction,5:0.00} {s.AimError,6:0.0}° | " +
                    $"{p.Skill,5:0.00} {p.BaseAccuracy,5:0.00} {p.HeadshotShare,4:0.00} {p.ReactionTime,5:0.00} {p.LookSpread,5:0.0} {p.TurnSpeed,4:0} {p.MemoryHalfLife,5:0.0}s {p.HearingError,3:0}m");
            }
            Console.WriteLine("  (Average = the replays' population; sense = Profile.Skill: memory, hearing, decision rate, reads, flick speed)");
            Console.WriteLine();
            Console.WriteLine($"archetypes  {"style",-8} {"aggr",4} {"coh",4} {"trust",5}");
            foreach (var x in BotArchetype.All)
                Console.WriteLine($"  {x.Name,-9} {x.Style,-8} {x.Aggression,4:0.00} {x.Cohesion,4:0.00} {x.Trust,5:0.00}  {x.Description}");
            Console.WriteLine();
            Console.WriteLine("lineups");
            foreach (var l in Bots.StyleLineups())
                Console.WriteLine($"  {l.Name,-10} {string.Join(", ", l.Bots.Select(b => $"{b.Name} {b.Profile.Cohesion:0.00}"))}   (archetype, cohesion)");
        }

        if (only.Contains("ladder"))
        {
            Console.WriteLine();
            Console.WriteLine("ladder: each tier's Standard lineup against Average's");
            BotHeader("tier");
            var average = Bots.Lineup("Average", BotTier.Average, Bots.Standard);
            foreach (var t in Bots.Tiers)
                BotRow(t.ToString(), Play(Bots.Lineup(t.ToString(), t, Bots.Standard), average));
        }

        if (only.Contains("brains"))
        {
            Console.WriteLine();
            Console.WriteLine("aim vs game sense: Standard lineups mixing one tier's aim with another's sense");
            var average = Bots.Lineup("Average", BotTier.Average, Bots.Standard);
            var aim = Bots.Lineup("Elite aim, Novice sense", BotTier.Elite, BotTier.Novice, Bots.Standard);
            var sense = Bots.Lineup("Novice aim, Elite sense", BotTier.Novice, BotTier.Elite, Bots.Standard);
            var aimOnly = Bots.Lineup("Elite aim, Average sense", BotTier.Elite, BotTier.Average, Bots.Standard);
            var senseOnly = Bots.Lineup("Average aim, Elite sense", BotTier.Average, BotTier.Elite, Bots.Standard);
            BotHeader("lineup (vs)", 37);
            BotRow($"{aimOnly.Name} (Average)", Play(aimOnly, average), 37);
            BotRow($"{senseOnly.Name} (Average)", Play(senseOnly, average), 37);
            BotRow($"{aim.Name} (Average)", Play(aim, average), 37);
            BotRow($"{sense.Name} (Average)", Play(sense, average), 37);
            BotRow($"{aim.Name} (the other)", Play(aim, sense), 37);
            BotRow("Average aim, Pro sense (Novice sense)", Play(Bots.Lineup("", BotTier.Average, BotTier.Pro, Bots.Standard),
                Bots.Lineup("", BotTier.Average, BotTier.Novice, Bots.Standard)), 37);
            BotLineup Trusting(float trust) { var l = Bots.Lineup("", BotTier.Average, Bots.Standard); foreach (var b in l.Bots) b.Profile.Trust = trust; return l; }
            BotRow("Trust 1 (trust 0), Average", Play(Trusting(1f), Trusting(0f)), 37);
            BotRow("Trust 1 (archetype trust), Average", Play(Trusting(1f), average), 37);
        }

        if (only.Contains("styles"))
        {
            Console.WriteLine();
            var lineups = Bots.StyleLineups();
            var win = new float[lineups.Count, lineups.Count];
            var byArchetype = new Dictionary<string, PlayerTally>();
            var total = new BotMatchResult[lineups.Count];
            void Collect(BotLineup l, PlayerTally[] tallies)
            {
                for (int i = 0; i < l.Bots.Length; i++)
                {
                    if (!byArchetype.TryGetValue(l.Bots[i].Name, out var t)) byArchetype[l.Bots[i].Name] = t = new PlayerTally();
                    t.Add(tallies[i]);
                }
            }
            for (int i = 0; i < lineups.Count; i++)
                for (int j = i + 1; j < lineups.Count; j++)
                {
                    var m = Play(lineups[i], lineups[j]);
                    win[i, j] = m.WinRateA;
                    win[j, i] = 1f - m.WinRateA;
                    Collect(lineups[i], m.TallyA);
                    Collect(lineups[j], m.TallyB);
                }
            Console.WriteLine("styles at Average skill: row lineup's round win rate against the column's (both sides)");
            Console.WriteLine($"  {"",-10} " + string.Join(" ", lineups.Select(l => $"{l.Name,9}")) + "    mean");
            for (int i = 0; i < lineups.Count; i++)
            {
                float mean = Enumerable.Range(0, lineups.Count).Where(j => j != i).Average(j => win[i, j]);
                Console.WriteLine($"  {lineups[i].Name,-10} " + string.Join(" ", Enumerable.Range(0, lineups.Count).Select(j => i == j ? $"{"-",9}" : $"{win[i, j],9:P0}")) + $"  {mean,6:P0}");
            }
            Console.WriteLine();
            Console.WriteLine("archetypes across the style matches (all Average skill)");
            Console.WriteLine($"  {"archetype",-10} {"rounds",6} {"K/r",5} {"D/r",5} {"K/D",5} {"open",5} {"1st K",5} {"1st D",5} {"trades",6} {"traded",6} {"acc",5} {"hs/hit",6}");
            foreach (var kv in byArchetype.OrderByDescending(kv => kv.Value.OpeningRate))
            {
                var t = kv.Value;
                Console.WriteLine($"  {kv.Key,-10} {t.Rounds,6} {t.Kills / (float)t.Rounds,5:0.00} {t.Deaths / (float)t.Rounds,5:0.00} {t.Kills / (float)Math.Max(1, t.Deaths),5:0.00} " +
                    $"{t.OpeningRate,5:P0} {t.FirstKillRate,5:P0} {t.FirstDeathRate,5:P0} {t.TradeRate,6:P0} {t.TradedRate,6:P0} {t.Accuracy,5:P0} {t.HeadHitRate,6:P0}");
            }
            Console.WriteLine("  (open: in the round's first duel; trades: kills that avenged a teammate; traded: deaths that were avenged)");
        }
        Console.WriteLine();
        Console.WriteLine($"{clock.Elapsed.TotalSeconds:0} s");
        return 0;
    }

    static void BotHeader(string label, int width = 10) =>
        Console.WriteLine($"  {label.PadRight(width)} {"win",5} {"+-",4} {"atk",5} {"def",5} {"plant",5} {"len",5} | {"K/r",5} {"D/r",5} {"K/D",5} {"1st K",5} {"traded",6} {"acc",5} {"hs/hit",6} {"react",5}");

    static void BotRow(string label, BotMatchResult m, int width = 10)
    {
        var t = m.TotalA;
        float rounds = m.Rounds;
        Console.WriteLine($"  {label.PadRight(width)} {m.WinRateA,5:P0} {m.HalfWidth,4:P0} {m.AttackWinRateA,5:P0} {m.DefenseWinRateA,5:P0} {m.Planted / rounds,5:P0} {m.MeanDuration,4:0}s | " +
            $"{t.Kills / rounds,5:0.00} {t.Deaths / rounds,5:0.00} {t.Kills / (float)Math.Max(1, t.Deaths),5:0.00} {t.FirstKills / rounds,5:P0} {t.TradedRate,6:P0} {t.Accuracy,5:P0} {t.HeadHitRate,6:P0} {t.ReactionMean,5:0.00}");
    }

    // ---------------------------------------------------------------- plans

    static int PlansCmd(List<string> a)
    {
        int n = int.Parse(Take(a, "-n") ?? "2", CultureInfo.InvariantCulture);
        int seed = int.Parse(Take(a, "--seed") ?? "0", CultureInfo.InvariantCulture);
        int threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture);
        var plans = (Take(a, "--attack") ?? "default,rush,slow,fake,latecall").Split(',').Select(Plans.Attack).ToList();
        if (!plans.Any(p => p.GetType() == typeof(DefaultAttack))) plans.Insert(0, new DefaultAttack());
        var options = Sets(a, null);
        var files = a.SelectMany<string, string>(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.vrp").OrderBy(f => f, StringComparer.Ordinal) : new[] { p }).ToList();
        if (files.Count == 0) throw new ArgumentException("at least one .vrp expected");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var replays = files.Select(VrpReplay.Load).ToList();
        var maps = replays.GroupBy(r => r.Map).ToDictionary(g => g.Key, g => SimMap.FromReplays(g));
        var starts = new List<(VrpReplay Replay, SimMap Map, float Time, Func<int, Profile> Profiles)>();
        foreach (var r in replays)
        {
            if (r.Players.Any(p => p.Team > 1)) continue;
            var fitted = ProfileFitter.Fit(r, map: maps[r.Map]);
            foreach (var rd in r.Rounds)
                if (rd.BarrierTime >= 0f && rd.AttackingTeam <= 1 && rd.BarrierTime < rd.EndTime) starts.Add((r, maps[r.Map], rd.BarrierTime, p => fitted[p].Profile));
        }
        int total = starts.Count * n;
        Console.WriteLine($"{files.Count} replays, {maps.Count} maps, {starts.Count} rounds from the barrier drop x {n} runs = {total:N0} simulated rounds per plan " +
            "(all AI, fitted profiles, the same seeds for every plan)");

        var results = plans.Select(plan => SimBatch.RunAll(i =>
        {
            var s = starts[i / n];
            return RoundSim.FromReplay(s.Replay, s.Map, s.Time, s.Profiles, options, SimRandom.RunSeed(seed, i % n, i / n), followRecording: false, attack: plan);
        }, 0, total, threads)).ToList();
        var baseline = results[plans.FindIndex(p => p.GetType() == typeof(DefaultAttack))];

        Console.WriteLine();
        Console.WriteLine($"  {"plan",-10} {"atk win",7} {"+-",4} {"vs Default",10} {"+-",4} {"plant",5} {"len",5} {"def rot",7} {"atk rot",7}");
        for (int k = 0; k < plans.Count; k++)
        {
            var rs = results[k];
            int wins = rs.Count(r => r.AttackersWon);
            // round for round against Default on the same seed (common random numbers): the paired difference's 95% interval
            var diff = rs.Select((r, i) => (r.AttackersWon ? 1.0 : 0.0) - (baseline[i].AttackersWon ? 1.0 : 0.0)).ToList();
            double mean = diff.Average(), sd = Math.Sqrt(diff.Sum(d => (d - mean) * (d - mean)) / Math.Max(1, diff.Count - 1));
            double half = 1.96 * sd / Math.Sqrt(diff.Count);
            Console.WriteLine($"  {plans[k].Name,-10} {wins / (float)total,7:P0} {BatchResult.WilsonHalfWidth(wins, total),4:P0} {mean,10:+0.0%;-0.0%;0.0%} {half,4:P0} " +
                $"{rs.Count(r => r.Planted) / (float)total,5:P0} {rs.Average(r => r.Duration),4:0}s {rs.Count(r => r.DefenseRotations > 0) / (float)total,7:P0} {rs.Count(r => r.AttackRotations > 0) / (float)total,7:P0}");
        }
        Console.WriteLine("  (vs Default: change in attacker win rate, round for round on the same seeds; def / atk rot: rounds in which defenders / attackers rotated)");
        Console.WriteLine();
        Console.WriteLine($"{clock.Elapsed.TotalSeconds:0} s");
        return 0;
    }

    // ---------------------------------------------------------------- calibrate

    static int CalibrateCmd(List<string> a)
    {
        var o = new CalibrationOptions
        {
            Runs = int.Parse(Take(a, "-n") ?? "100", CultureInfo.InvariantCulture),
            Seed = int.Parse(Take(a, "--seed") ?? "0", CultureInfo.InvariantCulture),
            Threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture),
        };
        string at = Take(a, "--at");
        if (at != null) o.Offsets = at.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        o.Profiles = (Take(a, "--profiles") ?? "loo") switch
        {
            "loo" => ProfileSource.LeaveRoundOut,
            "fitted" => ProfileSource.Fitted,
            "default" => ProfileSource.Default,
            var v => throw new ArgumentException($"--profiles {v}: loo, fitted or default"),
        };
        string csv = Take(a, "--csv");
        bool listPlayers = Flag(a, "--players");
        o.Measured = !Flag(a, "--knobs");
        foreach (var x in (Take(a, "--neutral") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (x == "decisions") o.FittedDecisions = false;
            else if (x == "sense") o.FittedSense = false;
            else throw new ArgumentException($"--neutral {x}: decisions or sense");
        }
        o.Learned = ParseLearned(Take(a, "--learned"));
        (o.Sim, o.Transfer) = Perception(Take(a, "--perception"));
        o.Sim = Attacks(a, Sets(a, o.Sim));
        if (a.Count == 0) throw new ArgumentException("at least one .vrp expected");

        var replays = a.Select(VrpReplay.Load).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int lastPct = -1;
        var rep = Calibration.Run(replays, o, Console.IsErrorRedirected ? null : (done, total) =>
        {
            int pct = done * 100 / total;
            if (pct / 10 != lastPct / 10) { lastPct = pct; Console.Error.Write($"\r  {pct}% of {total} snapshots"); }
        });
        if (!Console.IsErrorRedirected) Console.Error.WriteLine();
        var all = rep.Snapshots;
        string profiles = o.Profiles switch { ProfileSource.LeaveRoundOut => "fitted, leaving the round out", ProfileSource.Fitted => "fitted (incl. the round)", _ => "default" }
            + (o.Profiles != ProfileSource.Default && !o.Measured ? ", knobs only" : "")
            + (!o.FittedDecisions ? ", neutral decisions" : "") + (!o.FittedSense ? ", neutral sense" : "") + $"; learned {o.Learned.ToString().ToLowerInvariant()}; transfer {o.Transfer ?? ProfileFitter.Transfer}";
        Console.WriteLine($"{rep.Replays} replays ({string.Join(", ", replays.GroupBy(r => MapName(r)).Select(g => $"{g.Key} {g.Count()}"))}), " +
            $"{rep.Rounds} rounds, {all.Count} snapshots x {o.Runs} runs = {rep.SimulatedRounds:N0} simulated rounds in {clock.Elapsed.TotalSeconds:0} s; profiles {profiles}");

        Console.WriteLine();
        Console.WriteLine("attacker win   n     Brier  skill vs corpus  vs man-adv  log loss   (skill > 0 = better than the baseline)");
        void WinRow(string label, List<SnapshotResult> s) =>
            Console.WriteLine($"  {label,-10} {s.Count,4}  {CalibrationReport.Brier(s, x => x.WinRate),6:0.000}  {CalibrationReport.Skill(s, x => x.BaselineWin),15:+0.000;-0.000}  " +
                $"{CalibrationReport.Skill(s, x => x.ManAdvantageWin),10:+0.000;-0.000}  {CalibrationReport.LogLoss(s),8:0.000}");
        WinRow("all", all);
        foreach (var g in all.GroupBy(x => x.Offset).OrderBy(g => g.Key)) WinRow($"+{g.Key:0}s", g.ToList());

        Console.WriteLine();
        Console.WriteLine("reliability    n  predicted  observed");
        foreach (var b in rep.Reliability())
            Console.WriteLine($"  {b.Lo,4:P0}-{b.Hi,-5:P0} {b.Count,4}  {(b.Count > 0 ? b.Predicted.ToString("P0") : "-"),9}  {(b.Count > 0 ? b.Observed.ToString("P0") : "-"),8}");

        Console.WriteLine();
        Console.WriteLine("from the snapshot on      recorded  simulated   PIT mean  KS    (PIT 0.5 = centred; < 0.5: sim longer/more, > 0.5: shorter/fewer)");
        Console.WriteLine($"  time to decide          {CalibrationReport.Mean(all, x => x.Duration),7:0.0}s  {CalibrationReport.Mean(all, x => x.MeanDuration),8:0.0}s   " +
            $"{CalibrationReport.Mean(all, x => x.DurationPit),8:0.00}  {CalibrationReport.KsUniform(all.Select(x => x.DurationPit)),4:0.00}");
        var fk = all.Where(x => x.FirstKill >= 0f).ToList();
        var fkSim = all.Where(x => x.MedianFirstKill >= 0f).ToList();
        Console.WriteLine($"  time to next kill       {CalibrationReport.Mean(fk, x => x.FirstKill),7:0.0}s  {CalibrationReport.Mean(fkSim, x => x.MedianFirstKill),8:0.0}s   " +
            $"{CalibrationReport.Mean(all, x => x.FirstKillPit),8:0.00}  {CalibrationReport.KsUniform(all.Select(x => x.FirstKillPit)),4:0.00}   (sim: median per snapshot)");
        Console.WriteLine($"  no kill before decided  {CalibrationReport.Mean(all, x => x.FirstKill < 0f ? 1f : 0f),8:P0}  {CalibrationReport.Mean(all, x => x.NoKillRate),9:P0}");
        Console.WriteLine($"  kills                   {CalibrationReport.Mean(all, x => (float)x.Kills),7:0.00}   {CalibrationReport.Mean(all, x => x.MeanKills),8:0.00}    " +
            $"{CalibrationReport.Mean(all, x => x.KillsPit),8:0.00}  {CalibrationReport.KsUniform(all.Select(x => x.KillsPit)),4:0.00}");
        var unplanted = all.Where(x => !x.PlantedAtStart).ToList();
        Console.WriteLine($"  plant (not yet planted) {CalibrationReport.Mean(unplanted, x => x.Planted ? 1f : 0f),8:P0}  {CalibrationReport.Mean(unplanted, x => x.PlantRate),9:P0}");
        Console.WriteLine($"  attackers win           {CalibrationReport.Mean(all, x => x.Outcome),8:P0}  {CalibrationReport.Mean(all, x => x.WinRate),9:P0}");

        Console.WriteLine();
        Console.WriteLine("tempo (barrier snapshots)         recorded  simulated   PIT mean  KS    (never = after everything)");
        var bar = all.Where(x => x.Offset == o.Offsets.Min()).ToList();
        float Median(IEnumerable<float> v) { var l = v.OrderBy(x => x).ToList(); return l.Count > 0 ? l[l.Count / 2] : float.NaN; }
        Console.WriteLine($"  attack executes (2 on a site) {CalibrationReport.Mean(bar, x => x.ExecuteTime >= 0f ? 1f : 0f),8:P0}  {CalibrationReport.Mean(bar, x => x.ExecuteRate),9:P0}   " +
            $"{CalibrationReport.Mean(bar, x => x.ExecutePit),8:0.00}  {CalibrationReport.KsUniform(bar.Select(x => x.ExecutePit)),4:0.00}");
        Console.WriteLine($"    median time when it does    {Median(bar.Where(x => x.ExecuteTime >= 0f).Select(x => x.ExecuteTime)),7:0.0}s  {Median(bar.Where(x => x.MedianExecute >= 0f).Select(x => x.MedianExecute)),8:0.0}s   (sim: median of per-round medians)");
        Console.WriteLine($"  plant time PIT                                          {CalibrationReport.Mean(bar, x => x.PlantPit),8:0.00}  {CalibrationReport.KsUniform(bar.Select(x => x.PlantPit)),4:0.00}");
        Console.WriteLine($"    median plant time           {Median(bar.Where(x => x.PlantTime >= 0f).Select(x => x.PlantTime)),7:0.0}s  {Median(bar.Where(x => x.MedianPlant >= 0f).Select(x => x.MedianPlant)),8:0.0}s   (sim: median of per-round medians)");
        var tr = rep.Tempo.Recorded;
        var ts = rep.Tempo.Simulated;
        Console.WriteLine($"  speed m/s before the plant, by 10 s of round: " + string.Join(" ", Enumerable.Range(0, RoundTempo.Bins).Select(b => $"{b * 10,4}s")));
        Console.WriteLine($"    attackers recorded                          " + string.Join(" ", Enumerable.Range(0, RoundTempo.Bins).Select(b => $"{tr.AttackerSpeedAt(b),5:0.00}")));
        Console.WriteLine($"    attackers simulated                         " + string.Join(" ", Enumerable.Range(0, RoundTempo.Bins).Select(b => $"{ts.AttackerSpeedAt(b),5:0.00}")));
        Console.WriteLine($"    defenders recorded                          " + string.Join(" ", Enumerable.Range(0, RoundTempo.Bins).Select(b => $"{tr.DefenderSpeedAt(b),5:0.00}")));
        Console.WriteLine($"    defenders simulated                         " + string.Join(" ", Enumerable.Range(0, RoundTempo.Bins).Select(b => $"{ts.DefenderSpeedAt(b),5:0.00}")));

        Console.WriteLine();
        Console.WriteLine("round ends (barrier snapshots)   recorded  simulated");
        var barrier = all.Where(x => x.Offset == o.Offsets.Min()).ToList();
        foreach (var reason in new[] { RoundEndReason.Elimination, RoundEndReason.Detonated, RoundEndReason.Defused, RoundEndReason.TimeExpired })
            Console.WriteLine($"  {reason,-30} {CalibrationReport.Mean(barrier, x => x.Reason == reason ? 1f : 0f),8:P0}  " +
                $"{CalibrationReport.Mean(barrier, x => x.SimReasons.TryGetValue(reason, out int c) ? c / (float)x.Runs : 0f),9:P0}");

        Console.WriteLine();
        Console.WriteLine($"players ({rep.Players.Count} player-matches, barrier snapshots)   recorded  simulated  corr r   MAE   (r: do fitted profiles keep players apart?)");
        void PlayerRow(string label, Func<PlayerTally, float> f, Func<PlayerTally, bool> has = null)
        {
            var v = rep.PlayerPairs(f, has);
            Console.WriteLine($"  {label,-44} {CalibrationReport.Mean(v, x => x.Item1),8:0.00}  {CalibrationReport.Mean(v, x => x.Item2),9:0.00}  " +
                $"{CalibrationReport.Correlation(v),6:0.00}  {CalibrationReport.Mean(v, x => Math.Abs(x.Item1 - x.Item2)),5:0.00}");
        }
        PlayerRow("kills per round", t => t.Kills / (float)t.Rounds);
        PlayerRow("deaths per round", t => t.Deaths / (float)t.Rounds);
        PlayerRow("opening duel rate", t => t.OpeningRate);
        PlayerRow("first kill rate", t => t.FirstKillRate);
        PlayerRow("traded rate (deaths avenged)", t => t.TradedRate);
        PlayerRow("accuracy (30+ bullets)", t => t.Accuracy, t => t.Bullets >= 30);
        PlayerRow("head share of hits (10+ hits)", t => t.HeadHitRate, t => t.Hits >= 10);
        PlayerRow("leg share of hits (10+ hits)", t => t.LegHitRate, t => t.Hits >= 10);
        PlayerRow("reaction s (5+ reactions)", t => t.ReactionMean, t => t.Reactions >= 5);
        PlayerRow("shoots after noticing", t => t.ShootOnNoticeRate, t => t.Notices >= 5);
        PlayerRow("aim error at notice, degrees", t => t.AimErrorMean, t => t.Notices >= 5);

        if (listPlayers)
        {
            Console.WriteLine();
            Console.WriteLine($"{"match",-8} {"#",2} {"player",-16} {"agent",-10} {"profile",-44} | {"rounds",6} {"K/r rec",7} {"sim",5} {"D/r rec",7} {"sim",5} {"open rec",8} {"sim",5}");
            foreach (var p in rep.Players)
            {
                float R(PlayerTally t, int v) => t.Rounds > 0 ? v / (float)t.Rounds : 0f;
                Console.WriteLine($"{Trunc(p.Match, 8),-8} {p.Player,2} {Trunc(p.Name, 16),-16} {Trunc(p.Agent, 10),-10} {p.Profile,-44} | {p.Recorded.Rounds,6} " +
                    $"{R(p.Recorded, p.Recorded.Kills),7:0.00} {R(p.Simulated, p.Simulated.Kills),5:0.00} {R(p.Recorded, p.Recorded.Deaths),7:0.00} {R(p.Simulated, p.Simulated.Deaths),5:0.00} " +
                    $"{p.Recorded.OpeningRate,8:P0} {p.Simulated.OpeningRate,5:P0}");
            }
        }

        if (csv != null)
        {
            using var w = new StreamWriter(csv);
            w.WriteLine("match,map,round,offset,attackers_alive,defenders_alive,planted_at_start,attackers_won,planted,reason,duration,first_kill,kills," +
                "runs,win_rate,plant_rate,mean_duration,mean_kills,no_kill_rate,median_first_kill,duration_pit,first_kill_pit,kills_pit,baseline_win,man_adv_win");
            string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            foreach (var x in all)
                w.WriteLine(string.Join(",", x.Match, x.Map, x.Round, F(x.Offset), x.AttackersAlive, x.DefendersAlive, x.PlantedAtStart ? 1 : 0,
                    x.AttackersWon ? 1 : 0, x.Planted ? 1 : 0, x.Reason, F(x.Duration), F(x.FirstKill), x.Kills, x.Runs, F(x.WinRate), F(x.PlantRate),
                    F(x.MeanDuration), F(x.MeanKills), F(x.NoKillRate), F(x.MedianFirstKill), F(x.DurationPit), F(x.FirstKillPit), F(x.KillsPit),
                    F(x.BaselineWin), F(x.ManAdvantageWin)));
            Console.WriteLine();
            Console.WriteLine($"per-snapshot rows: {csv}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- scorecard, compare

    static int ScorecardCmd(List<string> a)
    {
        int runs = int.Parse(Take(a, "-n") ?? "40", CultureInfo.InvariantCulture);
        var seeds = Seeds(Take(a, "--seeds") ?? "0");
        int threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture);
        string output = Take(a, "--out"), baseline = Take(a, "--compare"), markdown = Take(a, "--markdown"), tolerance = Take(a, "--tolerance");
        var sets = new List<string>();
        for (int i = a.IndexOf("--set"); i >= 0 && i + 1 < a.Count; i = a.IndexOf("--set", i + 1)) sets.Add(a[i + 1]);
        int attackAt = a.IndexOf("--attack");
        string attack = attackAt >= 0 && attackAt + 1 < a.Count ? a[attackAt + 1] : null;
        var sim = Attacks(a, Sets(a, null));
        var replays = ReplayFiles(a).Select(VrpReplay.Load).ToList();
        if (replays.Count == 0) throw new ArgumentException("at least one .vrp (or a folder of them) expected");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cards = new List<Scorecard>();
        foreach (int seed in seeds)
        {
            var o = new CalibrationOptions { Runs = runs, Seed = seed, Threads = threads, Sim = sim };
            cards.Add(Scorecard.From(Calibration.Run(replays, o)));
            Console.Error.WriteLine($"  seed {seed}: {clock.Elapsed.TotalSeconds:0} s");
        }
        var card = Scorecard.Combine(cards);
        card.Set("options", "profiles loo; learned holdout; perception default" + string.Concat(sets.Select(s => "; set " + s)) + (attack != null ? "; attack " + attack : ""));
        Console.WriteLine($"{card.Get("replays")} replays, {card.Get("snapshots")} snapshots x {runs} runs x {seeds.Count} seed(s) in {clock.Elapsed.TotalSeconds:0} s");
        if (output != null)
        {
            card.Save(output);
            Console.WriteLine($"scorecard: {output}");
        }
        if (baseline == null)
        {
            Console.WriteLine();
            Console.WriteLine($"  {"metric",-34} {"better",-6} {"target",8} {"mean",9} {"sd",8}");
            foreach (var m in card.Metrics)
                Console.WriteLine($"  {m.Name,-34} {m.Better.ToString().ToLowerInvariant(),-6} {(float.IsNaN(m.Target) ? "" : m.Target.ToString("0.000")),8} {m.Mean,9:0.0000} {(float.IsNaN(m.Sd) ? "" : m.Sd.ToString("0.0000")),8}");
            return 0;
        }
        Console.WriteLine();
        return CompareCards(Scorecard.Load(baseline), baseline, card, "this run", markdown, tolerance);
    }

    static int CompareCmd(List<string> a)
    {
        string markdown = Take(a, "--markdown"), tolerance = Take(a, "--tolerance");
        if (a.Count != 2) throw new ArgumentException("compare <baseline.tsv> <current.tsv> expected");
        return CompareCards(Scorecard.Load(a[0]), a[0], Scorecard.Load(a[1]), a[1], markdown, tolerance);
    }

    /// <summary>Prints every metric's change from baseline to current (and a GitHub summary with --markdown). Exit code 1
    /// when a metric regressed or went missing, or the two were not run on the same replays and settings.</summary>
    static int CompareCards(Scorecard baseline, string baselineName, Scorecard current, string currentName, string markdown, string tolerance)
    {
        float tol = tolerance != null ? float.Parse(tolerance, CultureInfo.InvariantCulture) : Scorecard.Tolerance;
        var mismatch = new[] { "replays", "rounds", "snapshots", "runs", "options" }
            .Where(k => baseline.Get(k) != current.Get(k)).Select(k => $"{k}: {baseline.Get(k)} vs {current.Get(k)}").ToList();
        var changes = Scorecard.Compare(baseline, current, tol);
        int regressed = changes.Count(c => c.Verdict == ScoreVerdict.Regressed || c.Verdict == ScoreVerdict.Missing);
        int improved = changes.Count(c => c.Verdict == ScoreVerdict.Improved);
        string Describe(Scorecard c) => $"{c.Seeds} seed(s) x {c.Get("runs")} runs";
        string summary = $"{regressed} regressed, {improved} improved, {changes.Count - regressed - improved} within the noise " +
            $"(beyond {tol:0.#} standard errors of the seed spread counts)";

        Console.WriteLine($"{currentName} ({Describe(current)}) against {baselineName} ({Describe(baseline)})");
        foreach (var m in mismatch) Console.WriteLine($"  NOT COMPARABLE: {m}; regenerate the baseline (vrpsim scorecard ... --out) if that is intended");
        Console.WriteLine($"  {"metric",-34} {"better",-6} {"target",7} {"baseline",9} {"current",9} {"change",9} {"z",6}");
        foreach (var c in changes)
            Console.WriteLine($"  {c.Name,-34} {c.Better.ToString().ToLowerInvariant(),-6} {(float.IsNaN(c.Target) ? "" : c.Target.ToString("0.000")),7} " +
                $"{c.Baseline,9:0.0000} {c.Current,9:0.0000} {c.Current - c.Baseline,9:+0.0000;-0.0000;0} {(float.IsNaN(c.Z) ? "" : c.Z.ToString("+0.0;-0.0;0")),6}  {Mark(c.Verdict)}");
        Console.WriteLine(summary + "  (z: change in badness over its noise, + = worse)");

        if (markdown != null)
        {
            using var w = new StreamWriter(markdown, append: true);
            w.WriteLine("### Simulation scorecard");
            w.WriteLine();
            w.WriteLine($"{currentName} ({Describe(current)}) against `{baselineName}` ({Describe(baseline)}): **{summary}**.");
            foreach (var m in mismatch) w.WriteLine($"\n**Not comparable**: {m}. Regenerate the baseline if that is intended.");
            void Table(IEnumerable<ScoreChange> rows)
            {
                w.WriteLine();
                w.WriteLine("| metric | better | target | baseline | current | z | |");
                w.WriteLine("|---|---|---:|---:|---:|---:|---|");
                foreach (var c in rows)
                    w.WriteLine($"| {c.Name} | {c.Better.ToString().ToLowerInvariant()} | {(float.IsNaN(c.Target) ? "" : c.Target.ToString("0.000"))} | {c.Baseline:0.0000} | {c.Current:0.0000} | " +
                        $"{(float.IsNaN(c.Z) ? "" : c.Z.ToString("+0.0;-0.0;0"))} | {Mark(c.Verdict)} |");
            }
            var notable = changes.Where(c => c.Verdict == ScoreVerdict.Regressed || c.Verdict == ScoreVerdict.Improved || c.Verdict == ScoreVerdict.Missing).ToList();
            if (notable.Count > 0) Table(notable);
            w.WriteLine();
            w.WriteLine("<details><summary>every metric</summary>");
            Table(changes);
            w.WriteLine();
            w.WriteLine("</details>");
            w.WriteLine();
        }
        return regressed > 0 || mismatch.Count > 0 ? 1 : 0;

        static string Mark(ScoreVerdict v) => v switch
        {
            ScoreVerdict.Regressed => "REGRESSED",
            ScoreVerdict.Improved => "improved",
            ScoreVerdict.Missing => "MISSING",
            ScoreVerdict.Worse => "worse (noise)",
            ScoreVerdict.Better => "better (noise)",
            _ => "",
        };
    }

    /// <summary>"0-4" or "0,1,2".</summary>
    static List<int> Seeds(string s)
    {
        var dash = s.Split('-');
        if (dash.Length == 2) { int lo = int.Parse(dash[0], CultureInfo.InvariantCulture), hi = int.Parse(dash[1], CultureInfo.InvariantCulture); return Enumerable.Range(lo, hi - lo + 1).ToList(); }
        return s.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>.vrp files, a folder standing for every .vrp in it (in name order).</summary>
    static List<string> ReplayFiles(List<string> a)
    {
        var files = new List<string>();
        foreach (var p in a)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*.vrp").OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(p)) files.Add(p);
            else throw new ArgumentException($"{p}: not found");
        }
        return files;
    }

    // ---------------------------------------------------------------- tune

    static int TuneCmd(List<string> a)
    {
        int n = int.Parse(Take(a, "-n") ?? "40", CultureInfo.InvariantCulture);
        int iterations = int.Parse(Take(a, "--iterations") ?? "3", CultureInfo.InvariantCulture);
        int seed = int.Parse(Take(a, "--seed") ?? "0", CultureInfo.InvariantCulture);
        int threads = int.Parse(Take(a, "--threads") ?? "0", CultureInfo.InvariantCulture);
        var learned = ParseLearned(Take(a, "--learned"));
        var (perception, _) = Perception(Take(a, "--perception"));
        if (a.Count == 0) throw new ArgumentException("at least one .vrp expected");
        var replays = a.Select(VrpReplay.Load).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"{"step",4} {"acc transfer",12} {"rt offset",9} | {"acc rec",7} {"sim",5} {"r",5} | {"hs rec",6} {"sim",5} {"r",5} | {"legs rec",8} {"sim",5} | " +
            $"{"rt rec",6} {"sim",5} {"r",5} | {"shoot rec",9} {"sim",5} | {"aim rec",7} {"sim",5} {"r",5} {"base",5} {"slope",5} | {"K/r r",5}");
        Console.WriteLine($"{"",4} {ProfileFitter.Transfer.Accuracy,12:0.000} {ProfileFitter.Transfer.ReactionOffset,9:0.000}   (start)");
        ProfileTuner.Tune(replays, iterations, n, seed, threads, s =>
        {
            var r = s.Recorded;
            var m = s.Simulated;
            Console.WriteLine($"{s.Iteration,4} {s.AccuracyTransfer,12:0.000} {s.ReactionOffset,9:0.000} | {r.Accuracy,7:P1} {m.Accuracy,5:P1} {s.AccuracyR,5:0.00} | " +
                $"{r.HeadHitRate,6:P1} {m.HeadHitRate,5:P1} {s.HeadR,5:0.00} | {r.LegHitRate,8:P1} {m.LegHitRate,5:P1} | {r.ReactionMean,6:0.00} {m.ReactionMean,5:0.00} {s.ReactionR,5:0.00} | " +
                $"{r.ShootOnNoticeRate,9:P0} {m.ShootOnNoticeRate,5:P0} | {r.AimErrorMean,6:0.0}° {m.AimErrorMean,4:0.0}° {s.AimR,5:0.00} {s.AimErrorBase,5:0.0} {s.AimErrorSlope,5:0.00} | {s.KillsR,5:0.00}");
        }, learned, perception);
        Console.WriteLine($"(rec/sim of each step: simulated with the constants of the line before; r: correlation across players)  {clock.Elapsed.TotalSeconds:0} s");
        Console.WriteLine();
        var x = ProfileFitter.Transfer;
        Console.WriteLine("ProfileFitter population (perception-independent):");
        Console.WriteLine($"        public static float AccuracyMean = {F(ProfileFitter.AccuracyMean)}f, AccuracyPrior = {F(ProfileFitter.AccuracyPrior)}f;");
        Console.WriteLine($"        public static float HeadHitMean = {F(ProfileFitter.HeadHitMean)}f, LegHitMean = {F(ProfileFitter.LegHitMean)}f, HitPrior = {F(ProfileFitter.HitPrior)}f;");
        Console.WriteLine("ExecutionTransfer preset for this perception:");
        Console.WriteLine($"            ReactionMean = {F(x.ReactionMean)}f, AimErrorMean = {F(x.AimErrorMean)}f, Accuracy = {F(x.Accuracy)}f, ReactionOffset = {F(x.ReactionOffset)}f, " +
            $"AimErrorBase = {F(x.AimErrorBase)}f, AimErrorSlope = {F(x.AimErrorSlope)}f,");
        Console.WriteLine($"Profile.DefaultLookSpread for it: {F(x.PlacementFor(x.AimErrorMean))}");
        return 0;

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Perception presets: the simulation's defaults, or ValoReplay round play's (its KnowledgeSettings: field of
    /// view 103 deg, sight 120 m, footsteps 40 m, gunshots 100 m, which RoundAi hands the sim), each with its tuned transfer.</summary>
    static (SimOptions, ExecutionTransfer) Perception(string v) => (v ?? "default") switch
    {
        "default" => (null, null),
        "valoreplay" => (new SimOptions { FovHalfAngle = 51.5f, MaxSightRange = 120f, FootstepRange = 40f, GunshotRange = 100f }, ExecutionTransfer.ValoReplay),
        _ => throw new ArgumentException($"--perception {v}: default or valoreplay"),
    };

    static LearnedSource ParseLearned(string v) => (v ?? "holdout") switch
    {
        "holdout" => LearnedSource.Holdout,
        "all" => LearnedSource.All,
        "none" => LearnedSource.None,
        _ => throw new ArgumentException($"--learned {v}: holdout, all or none"),
    };

    static float Clock(string s)
    {
        var p = s.Split(':');
        return p.Length == 2 ? int.Parse(p[0], CultureInfo.InvariantCulture) * 60 + float.Parse(p[1], CultureInfo.InvariantCulture) : float.Parse(s, CultureInfo.InvariantCulture);
    }

    static string V(Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
    static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];
}
