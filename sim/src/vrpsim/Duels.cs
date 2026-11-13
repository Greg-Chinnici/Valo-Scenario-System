// Duels.cs - who sees whom first and what it is worth, on recorded rounds and simulated ones measured the same way
// (vrpsim duels). A notice is an enemy coming into the 104 deg view with NavGrid line of sight (<= 80 m) after >= 1 s
// out of it; a duel opens with the first notice of a pair; it is decided when one kills the other within 5 s. Also time
// from a notice to the first aimed shot, the first shot's miss and overswing by the flick it needed, exposure (sight
// lines, being seen, distance to walls) and its correlation with skill per recorded player-match.
using System.Collections.Concurrent;
using System.Numerics;
using ValorantReplay.Playback;
using ValorantReplay.Sim;

static class Duels
{
    public static void Run(List<VrpReplay> reps, int simRuns, SimOptions options, bool listPlayers, int threads)
    {
        var maps = reps.GroupBy(r => r.Map).ToDictionary(g => g.Key, g => SimMap.FromReplays(g));
        var recorded = new Stats(); var simulated = new Stats();
        var players = new ConcurrentDictionary<(string, int), Exposure>();
        var fits = new ConcurrentDictionary<string, FittedProfile[]>();
        Parallel.ForEach(reps, new ParallelOptions { MaxDegreeOfParallelism = threads <= 0 ? Environment.ProcessorCount : threads }, r =>
        {
            var map = maps[r.Map];
            var fitted = fits[r.MatchId] = ProfileFitter.Fit(r, map: map, options: options);
            for (int ri = 0; ri < r.Rounds.Count; ri++)
            {
                var rd = r.Rounds[ri];
                if (rd.BarrierTime < 0 || rd.AttackingTeam > 1 || rd.BarrierTime >= rd.EndTime) continue;
                float end = rd.PlantTime >= 0 ? Math.Min(rd.EndTime, rd.PlantTime + 45f) : rd.EndTime;
                var st = Analysis.Run(Trace.FromReplay(r, rd.BarrierTime, end), map, (p, e) => players.GetOrAdd((r.MatchId, p), _ => new Exposure()).Add(e));
                lock (recorded) recorded.Add(st);
                for (int k = 0; k < simRuns; k++)
                {
                    var sim = RoundSim.FromReplay(r, map, rd.BarrierTime, p => fitted[p].Profile, options, seed: k, followRecording: false);
                    var s2 = Analysis.Run(Trace.FromSim(sim), map, null);
                    lock (simulated) simulated.Add(s2);
                }
            }
        });
        Console.WriteLine($"{reps.Count} replays; simulated {simRuns} runs per round from the barrier drop (fitted profiles, all AI)");
        Stats.Print(recorded, simulated);
        if (!listPlayers) return;

        var rows = reps.SelectMany(r => fits[r.MatchId].Select(f => (r.MatchId, f)))
            .Where(x => players.ContainsKey((x.MatchId, x.f.Player)) && x.f.Tally.Rounds >= 5)
            .Select(x => (f: x.f, e: players[(x.MatchId, x.f.Player)])).Where(x => x.e.Samples > 200).ToList();
        static double Corr(IEnumerable<(double, double)> v)
        {
            var l = v.ToList(); double ma = l.Average(q => q.Item1), mb = l.Average(q => q.Item2);
            return l.Sum(q => (q.Item1 - ma) * (q.Item2 - mb)) / Math.Sqrt(l.Sum(q => (q.Item1 - ma) * (q.Item1 - ma)) * l.Sum(q => (q.Item2 - mb) * (q.Item2 - mb)));
        }
        Console.WriteLine();
        Console.WriteLine($"recorded players ({rows.Count} player-matches with 5+ rounds): correlation of how they move with how good they are");
        Console.WriteLine($"  {"",34} {"K/D",6} {"K/round",8} {"accuracy",9} {"head/hit",9}");
        void Row(string name, Func<Exposure, double> f) => Console.WriteLine($"  {name,-34} {Corr(rows.Select(x => (f(x.e), (double)Math.Min(4f, x.f.KillDeath)))),6:+0.00;-0.00} " +
            $"{Corr(rows.Select(x => (f(x.e), (double)x.f.Tally.Kills / x.f.Tally.Rounds))),8:+0.00;-0.00} {Corr(rows.Where(x => x.f.Tally.Bullets >= 60).Select(x => (f(x.e), (double)x.f.Tally.Accuracy))),9:+0.00;-0.00} " +
            $"{Corr(rows.Where(x => x.f.Tally.Hits >= 15).Select(x => (f(x.e), (double)x.f.Tally.HeadHitRate))),9:+0.00;-0.00}");
        Row("share of time in enemy sight lines", e => e.Exposed / (double)e.Samples);
        Row("share of time actually seen", e => e.Seen / (double)e.Samples);
        Row("distance to nearest wall", e => e.Wall / e.WallSamples);
        Row("share of time within 1 m of a wall", e => e.NearWall / (double)e.WallSamples);
        Row("mean speed", e => e.Speed / e.Samples);
        Console.WriteLine($"  (means: in sight lines {rows.Average(x => x.e.Exposed / (double)x.e.Samples):P0}, seen {rows.Average(x => x.e.Seen / (double)x.e.Samples):P0}, " +
            $"wall {rows.Average(x => x.e.Wall / x.e.WallSamples):0.0} m, within 1 m of a wall {rows.Average(x => x.e.NearWall / (double)x.e.WallSamples):P0})");
    }

    sealed class Exposure
    {
        public long Samples, Exposed, Seen, NearWall, WallSamples; public double Wall, Speed;
        public void Add(Exposure o) { lock (this) { Samples += o.Samples; Exposed += o.Exposed; Seen += o.Seen; NearWall += o.NearWall; WallSamples += o.WallSamples; Wall += o.Wall; Speed += o.Speed; } }
    }

    sealed class Trace
    {
        public int N; public int[] Team; public float Start, End, Dt = 0.05f;
        public List<Vector3[]> Pos = new(); public List<float[]> Yaw = new(); public List<bool[]> Alive = new();
        public List<(float t, int p, float yaw)> Shots = new();
        public List<(float t, int a, int v)> Hits = new();
        public List<(float t, int k, int v)> Kills = new();
        public int Ticks => Pos.Count;

        public static Trace FromReplay(VrpReplay r, float start, float end)
        {
            int n = r.Players.Count;
            var t = new Trace { N = n, Team = r.Players.Select(p => (int)p.Team).ToArray(), Start = start, End = end };
            for (float time = start; time < end; time += t.Dt)
            {
                var pos = new Vector3[n]; var yaw = new float[n]; var alive = new bool[n];
                for (int p = 0; p < n; p++) { var f = r.Sample(p, time); pos[p] = SimSpace.FromReplay(r, f); yaw[p] = f.Yaw; alive[p] = f.Alive; }
                t.Pos.Add(pos); t.Yaw.Add(yaw); t.Alive.Add(alive);
            }
            foreach (var s in r.Shots) if (s.Time >= start && s.Time < end && s.Player < n && Measurements.CountsForAccuracy(s.Weapon))
            {
                float yaw = s.Directions.Count > 0 ? SimSpace.YawOf(new Vector3(s.Directions[0].X, 0f, s.Directions[0].Z)) : s.Yaw;
                t.Shots.Add((s.Time - start, s.Player, yaw));
            }
            foreach (var d in r.Damage) if (d.Time >= start && d.Time < end && d.Attacker < n && d.Victim < n && r.Players[d.Attacker].Team != r.Players[d.Victim].Team && Measurements.CountsForAccuracy(d.Weapon))
                t.Hits.Add((d.Time - start, d.Attacker, d.Victim));
            foreach (var k in r.Kills) if (k.Time >= start && k.Time < end && k.Killer < n && k.Victim < n && r.Players[k.Killer].Team != r.Players[k.Victim].Team)
                t.Kills.Add((k.Time - start, k.Killer, k.Victim));
            return t;
        }

        public static Trace FromSim(RoundSim sim)
        {
            int n = sim.Agents.Count;
            var t = new Trace { N = n, Team = sim.Agents.Select(a => a.Team).ToArray(), Start = 0f };
            var last = sim.Agents.Select(a => a.LastShotTime).ToArray();
            while (!sim.Over)
            {
                t.Pos.Add(sim.Agents.Select(a => a.Position).ToArray()); t.Yaw.Add(sim.Agents.Select(a => a.Yaw).ToArray()); t.Alive.Add(sim.Agents.Select(a => a.Alive).ToArray());
                sim.Step();
                for (int i = 0; i < n; i++) if (sim.Agents[i].LastShotTime > last[i]) { last[i] = sim.Agents[i].LastShotTime; t.Shots.Add((sim.Time, i, sim.Agents[i].Yaw)); }
            }
            t.End = sim.Time;
            foreach (var e in sim.Events) if (e.Type == SimEventType.Hit && e.Other >= 0) t.Hits.Add((e.Time, e.Other, e.Agent));
            foreach (var k in sim.Kills) if (k.Killer >= 0 && k.KillerTeam != k.VictimTeam) t.Kills.Add((k.Time, k.Killer, k.Victim));
            return t;
        }
    }

    sealed class Stats
    {
        public int Duels, OneSided, Simultaneous, FirstWon, FirstLost, Decided;
        public readonly List<float> Gaps = new();
        public readonly int[] GapDuels = new int[5], GapFirstWon = new int[5], GapDecided = new int[5];
        public readonly List<float> ReactFirst = new(), ReactSecond = new();
        public int FirstShots, FirstShotHits, Overshoots, Undershoots;
        public readonly int[] FlickShots = new int[4], FlickHits = new int[4], FlickOver = new int[4];
        public readonly double[] FlickErr = new double[4];
        public readonly int[] DistShots = new int[4], DistHits = new int[4];
        public readonly int[] OpenDist = new int[4];
        public int OpenBothStill, OpenOneMoving, OpenBothMoving;
        public long Samples, Exposed, Seen; public double Speed;
        public void Add(Stats o)
        {
            Duels += o.Duels; OneSided += o.OneSided; Simultaneous += o.Simultaneous; FirstWon += o.FirstWon; FirstLost += o.FirstLost; Decided += o.Decided;
            Gaps.AddRange(o.Gaps); ReactFirst.AddRange(o.ReactFirst); ReactSecond.AddRange(o.ReactSecond);
            for (int i = 0; i < 5; i++) { GapDuels[i] += o.GapDuels[i]; GapFirstWon[i] += o.GapFirstWon[i]; GapDecided[i] += o.GapDecided[i]; }
            FirstShots += o.FirstShots; FirstShotHits += o.FirstShotHits; Overshoots += o.Overshoots; Undershoots += o.Undershoots;
            for (int i = 0; i < 4; i++) { FlickShots[i] += o.FlickShots[i]; FlickHits[i] += o.FlickHits[i]; FlickOver[i] += o.FlickOver[i]; FlickErr[i] += o.FlickErr[i]; DistShots[i] += o.DistShots[i]; DistHits[i] += o.DistHits[i]; OpenDist[i] += o.OpenDist[i]; }
            OpenBothStill += o.OpenBothStill; OpenOneMoving += o.OpenOneMoving; OpenBothMoving += o.OpenBothMoving;
            Samples += o.Samples; Exposed += o.Exposed; Seen += o.Seen; Speed += o.Speed;
        }
        static float Med(List<float> l) { if (l.Count == 0) return float.NaN; var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        static float Q(List<float> l, double q) { if (l.Count == 0) return float.NaN; var s = l.OrderBy(x => x).ToList(); return s[(int)(q * (s.Count - 1))]; }
        public static readonly string[] GapNames = { "same tick (<0.05 s)", "0.05-0.25 s", "0.25-0.5 s", "0.5-1.5 s", "only one side noticed" };
        public static readonly string[] FlickNames = { "0-5 deg", "5-15 deg", "15-30 deg", "30+ deg" };
        public static readonly string[] DistNames = { "0-10 m", "10-20 m", "20-35 m", "35+ m" };
        public static void Print(Stats r, Stats s)
        {
            string P(int a, int b) => b > 0 ? (a / (double)b).ToString("P0") : "-";
            Console.WriteLine();
            Console.WriteLine($"duels (a pair notices each other; decided = one killed the other within 5 s)   recorded   simulated");
            Console.WriteLine($"  duels                                                   {r.Duels,9} {s.Duels,11}");
            Console.WriteLine($"  both noticed on the same tick                           {P(r.Simultaneous, r.Duels),9} {P(s.Simultaneous, s.Duels),11}");
            Console.WriteLine($"  only one noticed the other (within 1.5 s)               {P(r.OneSided, r.Duels),9} {P(s.OneSided, s.Duels),11}");
            Console.WriteLine($"  head start when both noticed: median                    {Med(r.Gaps),8:0.00}s {Med(s.Gaps),10:0.00}s");
            Console.WriteLine($"  decided duels: whoever noticed first won                {P(r.FirstWon, r.Decided),9} {P(s.FirstWon, s.Decided),11}");
            Console.WriteLine($"  by head start (decided duels, first noticer won / n):");
            for (int i = 0; i < 5; i++)
                Console.WriteLine($"    {GapNames[i],-24} {P(r.GapFirstWon[i], r.GapDecided[i]),6} of {r.GapDecided[i],5}   {P(s.GapFirstWon[i], s.GapDecided[i]),6} of {s.GapDecided[i],6}   (share of duels {P(r.GapDuels[i], r.Duels)} / {P(s.GapDuels[i], s.Duels)})");
            Console.WriteLine();
            Console.WriteLine($"time from noticing to the first aimed shot (within 10 deg, 1.5 s)   recorded             simulated");
            Console.WriteLine($"  first to notice    median {Med(r.ReactFirst):0.00}s  p25 {Q(r.ReactFirst, .25):0.00} p75 {Q(r.ReactFirst, .75):0.00} n {r.ReactFirst.Count,5}    median {Med(s.ReactFirst):0.00}s p25 {Q(s.ReactFirst, .25):0.00} p75 {Q(s.ReactFirst, .75):0.00} n {s.ReactFirst.Count}");
            Console.WriteLine($"  second to notice   median {Med(r.ReactSecond):0.00}s  p25 {Q(r.ReactSecond, .25):0.00} p75 {Q(r.ReactSecond, .75):0.00} n {r.ReactSecond.Count,5}    median {Med(s.ReactSecond):0.00}s p25 {Q(s.ReactSecond, .25):0.00} p75 {Q(s.ReactSecond, .75):0.00} n {s.ReactSecond.Count}");
            Console.WriteLine();
            Console.WriteLine($"first shot after a notice (within 30 deg, 1.5 s), by the flick it needed            recorded                       simulated");
            for (int i = 0; i < 4; i++)
                Console.WriteLine($"  {FlickNames[i],-10} n {r.FlickShots[i],5}  hit {P(r.FlickHits[i], r.FlickShots[i]),4}  overshot {P(r.FlickOver[i], r.FlickShots[i]),4}  mean miss {r.FlickErr[i] / Math.Max(1, r.FlickShots[i]),4:0.0} deg" +
                    $"    n {s.FlickShots[i],6}  hit {P(s.FlickHits[i], s.FlickShots[i]),4}  overshot {P(s.FlickOver[i], s.FlickShots[i]),4}  mean miss {s.FlickErr[i] / Math.Max(1, s.FlickShots[i]),4:0.0} deg");
            Console.WriteLine($"duels open at        " + string.Join("  ", Enumerable.Range(0, 4).Select(i => $"{DistNames[i]} {P(r.OpenDist[i], r.Duels)} / {P(s.OpenDist[i], s.Duels)}")) + "   (recorded / simulated)");
            Console.WriteLine($"duels open with      both still {P(r.OpenBothStill, r.Duels)} / {P(s.OpenBothStill, s.Duels)}  one moving {P(r.OpenOneMoving, r.Duels)} / {P(s.OpenOneMoving, s.Duels)}  both moving {P(r.OpenBothMoving, r.Duels)} / {P(s.OpenBothMoving, s.Duels)}   (moving: over 2 m/s)");
            Console.WriteLine("  by distance:");
            for (int i = 0; i < 4; i++)
                Console.WriteLine($"  {DistNames[i],-10} n {r.DistShots[i],5}  hit {P(r.DistHits[i], r.DistShots[i]),4}                                n {s.DistShots[i],6}  hit {P(s.DistHits[i], s.DistShots[i]),4}");
            Console.WriteLine();
            Console.WriteLine($"exposure (alive, every 0.05 s)        recorded   simulated");
            Console.WriteLine($"  in an enemy's sight line            {r.Exposed / (double)r.Samples,8:P0} {s.Exposed / (double)s.Samples,11:P0}");
            Console.WriteLine($"  seen (in an enemy's view)           {r.Seen / (double)r.Samples,8:P0} {s.Seen / (double)s.Samples,11:P0}");
            Console.WriteLine($"  mean speed                          {r.Speed / r.Samples,7:0.00} {s.Speed / s.Samples,10:0.00}  m/s");
        }
    }

    static class Analysis
    {
        const float Fov = 52f, Range = 80f, Gap = 1f, Window = 1.5f;
        public static float Delta(float a, float b) { float d = (b - a) % 360f; if (d > 180f) d -= 360f; if (d < -180f) d += 360f; return d; }

        public static Stats Run(Trace t, SimMap map, Action<int, Exposure> perPlayer)
        {
            var st = new Stats(); var g = map.Grid; int n = t.N;
            var inView = new bool[n, n]; var lastView = new float[n, n]; var noticeAt = new float[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { lastView[i, j] = -100f; noticeAt[i, j] = -100f; }
            var duelOpen = new float[n, n]; for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) duelOpen[i, j] = -100f;
            var exp = Enumerable.Range(0, n).Select(_ => new Exposure()).ToArray();
            var notices = new List<(float t, int a, int b, float yaw, float dir)>();
            var duels = new List<(float t, int first, int second, float gap)>();
            for (int k = 0; k < t.Ticks; k++)
            {
                float time = k * t.Dt;
                var pos = t.Pos[k]; var yaw = t.Yaw[k]; var alive = t.Alive[k];
                var exposed = new bool[n]; var seen = new bool[n];
                for (int a = 0; a < n; a++)
                    for (int b = a + 1; b < n; b++)
                    {
                        if (t.Team[a] == t.Team[b]) continue;
                        bool los = false;
                        if (alive[a] && alive[b])
                        {
                            float d = Vector3.Distance(pos[a], pos[b]);
                            los = d <= Range && g.LineOfSight(pos[a] + new Vector3(0, 0.65f, 0), pos[b] + new Vector3(0, 0.65f, 0));
                        }
                        if (los) { exposed[a] = exposed[b] = true; }
                        foreach (var (x, y) in new[] { (a, b), (b, a) })
                        {
                            var to = pos[y] - pos[x];
                            float dir = SimSpace.YawOf(to);
                            bool view = los && (new Vector2(to.X, to.Z).Length() < 3f || Math.Abs(Delta(yaw[x], dir)) <= Fov);
                            if (view) seen[y] = true;
                            if (view && !inView[x, y] && time - lastView[x, y] >= Gap)
                            {
                                noticeAt[x, y] = time;
                                notices.Add((time, x, y, yaw[x], dir));
                                // a duel opens with the first notice of the pair (none open in the last 3 s)
                                int lo = Math.Min(x, y), hi = Math.Max(x, y);
                                if (time - duelOpen[lo, hi] > 3f)
                            {
                                duelOpen[lo, hi] = time; duels.Add((time, x, y, float.NaN));
                                float od = Vector3.Distance(pos[x], pos[y]);
                                st.OpenDist[od < 10f ? 0 : od < 20f ? 1 : od < 35f ? 2 : 3]++;
                                int moving = 0;
                                if (k > 0) foreach (int q in new[] { x, y }) if (SimSpace.FlatDistance(pos[q], t.Pos[k - 1][q]) / t.Dt > 2f) moving++;
                                if (moving == 0) st.OpenBothStill++; else if (moving == 1) st.OpenOneMoving++; else st.OpenBothMoving++;
                            }
                                else
                                {
                                    int di = duels.FindLastIndex(q => Math.Min(q.first, q.second) == lo && Math.Max(q.first, q.second) == hi);
                                    if (di >= 0 && float.IsNaN(duels[di].gap) && duels[di].second == x && time - duels[di].t <= Window) duels[di] = (duels[di].t, duels[di].first, x, time - duels[di].t);
                                }
                            }
                            if (view) lastView[x, y] = time;
                            inView[x, y] = view;
                        }
                    }
                for (int p = 0; p < n; p++)
                {
                    if (!alive[p]) continue;
                    var e = exp[p]; e.Samples++; if (exposed[p]) e.Exposed++; if (seen[p]) e.Seen++;
                    if (k > 0 && t.Alive[k - 1][p]) { float sp = SimSpace.FlatDistance(pos[p], t.Pos[k - 1][p]) / t.Dt; if (sp < 8f) e.Speed += sp; }
                    if (k % 10 == 0 && perPlayer != null) { float w = WallDist(g, pos[p]); e.Wall += w; e.WallSamples++; if (w <= 1f) e.NearWall++; }
                }
            }
            foreach (var e in exp) { st.Samples += e.Samples; st.Exposed += e.Exposed; st.Seen += e.Seen; st.Speed += e.Speed; }
            if (perPlayer != null) for (int p = 0; p < n; p++) perPlayer(p, exp[p]);

            // duels: who noticed first, and who killed whom within 5 s
            foreach (var d in duels)
            {
                st.Duels++;
                int bin = float.IsNaN(d.gap) ? 4 : d.gap < 0.05f ? 0 : d.gap < 0.25f ? 1 : d.gap < 0.5f ? 2 : 3;
                if (bin == 4) st.OneSided++; else if (bin == 0) st.Simultaneous++; else st.Gaps.Add(d.gap);
                st.GapDuels[bin]++;
                int a = d.first, b = d.second;
                int other = float.IsNaN(d.gap) ? b : d.second;
                int ki = t.Kills.FindIndex(q => q.t >= d.t && q.t <= d.t + 5f && ((q.k == a && q.v == other) || (q.k == other && q.v == a)));
                if (ki < 0) continue;
                var kill = t.Kills[ki];
                st.Decided++; st.GapDecided[bin]++;
                if (kill.Item2 == a) { st.FirstWon++; st.GapFirstWon[bin]++; } else st.FirstLost++;
            }

            // reactions and first-shot flicks
            var shotsBy = t.Shots.GroupBy(s => s.p).ToDictionary(q => q.Key, q => q.OrderBy(s => s.t).ToList());
            foreach (var nt in notices)
            {
                if (!shotsBy.TryGetValue(nt.a, out var list)) continue;
                int si = list.FindIndex(s => s.t >= nt.t);
                if (si < 0 || list[si].t > nt.t + Window) continue;
                var shot = list[si];
                int k = Math.Min(t.Ticks - 1, (int)Math.Round(shot.t / t.Dt));
                float dirNow = SimSpace.YawOf(t.Pos[k][nt.b] - t.Pos[k][nt.a]);
                float err = Delta(dirNow, shot.yaw);
                bool firstNoticer = duels.Any(d => d.first == nt.a && d.second == nt.b && Math.Abs(d.t - nt.t) < 0.001f);
                if (Math.Abs(err) <= 10f) (firstNoticer ? st.ReactFirst : st.ReactSecond).Add(shot.t - nt.t);
                if (Math.Abs(err) > 30f) continue;
                float flick = Delta(nt.yaw, nt.dir);
                int fb = Math.Abs(flick) < 5f ? 0 : Math.Abs(flick) < 15f ? 1 : Math.Abs(flick) < 30f ? 2 : 3;
                bool hit = t.Hits.Any(h => h.a == nt.a && h.v == nt.b && h.t >= shot.t - 0.001f && h.t <= shot.t + 0.15f);
                st.FlickShots[fb]++; if (hit) st.FlickHits[fb]++; st.FlickErr[fb] += Math.Abs(err);
                float dist = Vector3.Distance(t.Pos[k][nt.a], t.Pos[k][nt.b]);
                int db = dist < 10f ? 0 : dist < 20f ? 1 : dist < 35f ? 2 : 3;
                st.DistShots[db]++; if (hit) st.DistHits[db]++;
                if (fb > 0 && Math.Abs(err) > 0.5f && Math.Sign(err) == Math.Sign(flick)) st.FlickOver[fb]++;
            }
            return st;
        }

        static float WallDist(NavGrid g, Vector3 p)
        {
            float cell = g.CellSize;
            for (int r = 1; r <= 8; r++)
                for (int dirI = 0; dirI < 8; dirI++)
                {
                    double ang = dirI * Math.PI / 4;
                    float x = p.X + (float)Math.Cos(ang) * r * cell, z = p.Z + (float)Math.Sin(ang) * r * cell;
                    bool floor = false;
                    foreach (int nd in g.NodesAt(x, z)) if (Math.Abs(g.Nodes[nd].Y - p.Y) < 1f) { floor = true; break; }
                    if (!floor) return r * cell;
                }
            return 8 * cell;
        }
    }
}
