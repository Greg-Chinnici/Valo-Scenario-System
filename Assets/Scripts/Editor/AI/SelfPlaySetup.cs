using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using ValoReplay.Maps;

namespace ValoReplay.EditorTools.AI
{
    /// <summary>Where self-play's agents get their execution (aim, reaction, crosshair placement) from.</summary>
    public enum SelfPlayProfiles
    {
        /// <summary>The presets' style, aggression, cohesion and skill with the population's measured execution under
        /// round play's perception (ProfileFitter's population values through ExecutionTransfer.ValoReplay).</summary>
        Population,
        /// <summary>The presets' style, aggression and cohesion with the execution and skill fitted from the players of
        /// the given replay (ProfileFitter, as round play fits them).</summary>
        ReplayPlayers,
        /// <summary>The presets as they are: execution from the Skill knob alone. Not what round play runs; kept to
        /// compare with self-play reports from before measured execution.</summary>
        Knobs,
    }

    /// <summary>
    /// What self-play's agents play with besides the map, chosen to match what round play (RoundAi) runs:
    /// <list type="bullet">
    /// <item>Attack timing: a TempoModel from replays of this map when a replay or folder is given, else from every
    /// replay of every map in the given location and the replay library (upstream calibration falls back across maps the
    /// same way), else none (TeamDirector's fixed execute at 55 s left).</item>
    /// <item>Where agents look: a LookModel from replays of this map (it is learned per map position, so other maps'
    /// replays say nothing about this one), else none.</item>
    /// <item>Profiles: the five presets (Entry, Support, Lurker, Anchor, Balanced) per team with measured execution.</item>
    /// </list>
    /// </summary>
    public sealed class SelfPlaySetup
    {
        public LookModel Look;
        public TempoModel Tempo;
        /// <summary>One line each, for the report.</summary>
        public string TempoSource = "", LookSource = "", ProfileSource = "";
        /// <summary>Short labels for each batch's summary.</summary>
        public string TempoLabel = "", ProfileLabel = "";
        public Profile[] Attackers = SelfPlay.Presets, Defenders = SelfPlay.Presets;
        /// <summary>Per slot, what the profile is (team 0 = attackers, then defenders).</summary>
        public readonly List<string> SlotLines = new List<string>();

        public const string FixedTempo = "fixed: execute at 55 s left (TeamDirector default, no TempoModel)";

        /// <summary>The geometry map with the learned behaviour attached.</summary>
        public SimMap Apply(SimMap map) => map.WithLearned(Look, Tempo);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AI setup (what round play runs, as far as a map without its replay allows):");
            sb.AppendLine($"  tempo     {TempoSource}");
            sb.AppendLine($"  looks     {LookSource}");
            sb.AppendLine($"  profiles  {ProfileSource}");
            foreach (var l in SlotLines) sb.AppendLine($"    {l}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ building it

        /// <summary>Replays to learn from: of this map from the given file or folder, and (on demand) every loadable
        /// replay there and in the replay library.</summary>
        public sealed class ReplaySet
        {
            public string Given = "";
            public readonly List<VrpReplay> SameMap = new List<VrpReplay>();
            public readonly List<string> Folders = new List<string>();
            List<VrpReplay> corpus;
            readonly Dictionary<string, VrpReplay> loaded = new Dictionary<string, VrpReplay>();

            /// <summary>The first replay of this map (what the grid comparison and replay-fitted profiles use).</summary>
            public VrpReplay First => SameMap.Count > 0 ? SameMap[0] : null;

            public static ReplaySet Load(string path, string levelPath)
            {
                var set = new ReplaySet { Given = path ?? "" };
                string want = MapCatalog.LevelName(levelPath ?? "");
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var r = set.Get(Path.GetFullPath(path));
                    if (r != null && (want == "" || MapCatalog.LevelName(r.Map) == want)) set.SameMap.Add(r);
                    set.AddFolder(Path.GetDirectoryName(Path.GetFullPath(path)));
                }
                else if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    set.AddFolder(path);
                    foreach (var f in Directory.GetFiles(path, "*.vrp").OrderBy(f => f))
                    {
                        var r = set.Get(Path.GetFullPath(f));
                        if (r == null) continue;
                        // an open scene with no catalog entry takes the map of the first replay
                        if (want == "") want = MapCatalog.LevelName(r.Map);
                        if (MapCatalog.LevelName(r.Map) == want && set.SameMap.All(o => o.MatchId != r.MatchId)) set.SameMap.Add(r);
                    }
                }
                set.AddFolder(Path.Combine(Application.persistentDataPath, "Replays"));
                return set;
            }

            void AddFolder(string folder)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                folder = Path.GetFullPath(folder);
                if (!Folders.Contains(folder)) Folders.Add(folder);
            }

            VrpReplay Get(string file)
            {
                if (loaded.TryGetValue(file, out var r)) return r;
                try { r = VrpReplay.Load(file); }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is EndOfStreamException)
                {
                    Debug.LogWarning($"[SelfPlay] {file}: {e.Message}");
                    r = null;
                }
                loaded[file] = r;
                return r;
            }

            /// <summary>Every loadable replay of any map in the given location and the replay library, one per match.</summary>
            public List<VrpReplay> Corpus()
            {
                if (corpus != null) return corpus;
                corpus = new List<VrpReplay>();
                var matches = new HashSet<string>();
                foreach (var r in SameMap) if (matches.Add(r.MatchId)) corpus.Add(r);
                foreach (var folder in Folders)
                    foreach (var f in Directory.GetFiles(folder, "*.vrp").OrderBy(f => f))
                    {
                        var r = Get(Path.GetFullPath(f));
                        if (r != null && matches.Add(r.MatchId)) corpus.Add(r);
                    }
                return corpus;
            }
        }

        /// <param name="replayMap">SimMap.FromReplays of replays.First (line of sight for fitting players' reactions).</param>
        public static SelfPlaySetup Create(string mapName, ReplaySet replays, SelfPlayProfiles profiles, SimMap replayMap)
        {
            var s = new SelfPlaySetup();
            s.LearnTempoAndLooks(mapName, replays);
            s.ChooseProfiles(replays, profiles, replayMap);
            return s;
        }

        void LearnTempoAndLooks(string mapName, ReplaySet replays)
        {
            string tempoWhy = "no replay given";
            if (replays.SameMap.Count > 0)
            {
                var look = LookModel.FromReplays(replays.SameMap);
                if (look.Samples > 0)
                {
                    Look = look;
                    LookSource = $"LookModel from {replays.SameMap.Count} replay(s) of {mapName} ({look.Samples} view samples)";
                }
                var tempo = TempoModel.FromReplays(replays.SameMap);
                if (tempo.Known)
                {
                    Tempo = tempo;
                    TempoSource = $"TempoModel from {replays.SameMap.Count} replay(s) of {mapName}: {Plants(tempo)}";
                    TempoLabel = $"{mapName} replays ({tempo.PlantTimes.Count} plants)";
                }
                else tempoWhy = $"{replays.SameMap.Count} replay(s) of {mapName} have {tempo.PlantTimes.Count} plants (< {TempoModel.MinPlants})";
            }
            else if (!string.IsNullOrEmpty(replays.Given)) tempoWhy = $"no replay of {mapName} at {replays.Given}";
            if (Look == null) LookSource = "none: agents look where they walk or at their objective (a LookModel needs replays of this map)";

            if (Tempo == null)
            {
                var corpus = replays.Corpus();
                var tempo = TempoModel.FromReplays(corpus);
                if (tempo.Known)
                {
                    Tempo = tempo;
                    int maps = corpus.Select(r => MapCatalog.LevelName(r.Map)).Distinct().Count();
                    TempoSource = $"corpus-wide TempoModel ({tempoWhy}): {corpus.Count} replay(s) of {maps} map(s) in {string.Join(", ", replays.Folders)}: {Plants(tempo)}";
                    TempoLabel = $"corpus-wide ({tempo.PlantTimes.Count} plants, {maps} maps)";
                }
                else
                {
                    TempoSource = $"{FixedTempo}: {tempoWhy}, and {corpus.Count} replay(s) in the library have {tempo.PlantTimes.Count} plants";
                    TempoLabel = "fixed 55 s left";
                }
            }
        }

        /// <summary>Plant times and the execute times they give (t = seconds after the barrier drop).</summary>
        static string Plants(TempoModel tempo)
        {
            var p = tempo.PlantTimes.OrderBy(x => x).ToList();
            float Q(float q) => p[(int)(q * (p.Count - 1))];
            return $"{p.Count} plants at t median {Q(0.5f):0} s (p10 {Q(0.1f):0}, p90 {Q(0.9f):0}); the attack executes at a drawn plant time − " +
                   $"{TempoModel.ExecuteLead:0} s, median t {Math.Max(0f, Q(0.5f) - TempoModel.ExecuteLead):0} s (fixed timing: t 45 s)";
        }

        void ChooseProfiles(ReplaySet replays, SelfPlayProfiles profiles, SimMap replayMap)
        {
            if (profiles == SelfPlayProfiles.ReplayPlayers && (replays.First == null || replayMap == null))
            {
                ProfileSource = "(no replay of this map given, so not fitted from players) ";
                profiles = SelfPlayProfiles.Population;
            }
            switch (profiles)
            {
                case SelfPlayProfiles.Knobs:
                    Attackers = Defenders = SelfPlay.Presets;
                    ProfileSource += "presets with execution from the Skill knob alone (unmeasured: not what round play runs, which fits measured execution)";
                    ProfileLabel = "unmeasured presets";
                    break;
                case SelfPlayProfiles.Population:
                    var population = ProfileFitter.ToProfile(new FittedProfile(), ExecutionTransfer.ValoReplay);
                    Attackers = Defenders = SelfPlay.Presets.Select(p => WithExecution(p, population, false)).ToArray();
                    ProfileSource += "presets (style, aggression, cohesion, skill) with the population's measured execution under round play's " +
                                     $"perception (ProfileFitter population through ExecutionTransfer.ValoReplay: {ExecutionTransfer.ValoReplay})";
                    ProfileLabel = "presets + population execution";
                    break;
                case SelfPlayProfiles.ReplayPlayers:
                    FitFromPlayers(replays.First, replayMap);
                    break;
            }
            if (profiles != SelfPlayProfiles.ReplayPlayers)   // the same five on both teams
                for (int k = 0; k < Attackers.Length; k++) SlotLines.Add($"atk{k}/def{k} {Describe(Attackers[k])}");
        }

        /// <summary>Each self-play team takes a replay team's players (replay team 0 attacks), matched to the presets by
        /// play style where it can; execution and skill come from the player, style, aggression and cohesion from the
        /// preset.</summary>
        void FitFromPlayers(VrpReplay replay, SimMap replayMap)
        {
            var fitted = ProfileFitter.Fit(replay, map: replayMap, options: SelfPlay.Options(), transfer: ExecutionTransfer.ValoReplay);
            var population = ProfileFitter.ToProfile(new FittedProfile(), ExecutionTransfer.ValoReplay);
            var lines = new List<string>();
            for (int team = 0; team < 2; team++)
            {
                var pool = fitted.Where(f => replay.Players[f.Player].Team == team).ToList();
                var result = new Profile[SelfPlay.Presets.Length];
                for (int k = 0; k < result.Length; k++)
                {
                    var preset = SelfPlay.Presets[k];
                    var f = pool.FirstOrDefault(x => x.Profile.Style == preset.Style) ?? pool.FirstOrDefault();
                    string who;
                    if (f != null)
                    {
                        pool.Remove(f);
                        result[k] = WithExecution(preset, f.Profile, true);
                        who = $"← {(f.Name != f.Agent && f.Name != "" ? f.Name + " " : "")}({f.Agent}, {f.Tally.Rounds} rounds, {f.Tally.Bullets} bullets, {f.Tally.Reactions} reactions)";
                    }
                    else
                    {
                        result[k] = WithExecution(preset, population, false);
                        who = "← nobody left on the replay team: population execution";
                    }
                    lines.Add($"{(team == 0 ? "atk" : "def")}{k} {Describe(result[k])} {who}");
                }
                if (team == 0) Attackers = result; else Defenders = result;
            }
            SlotLines.AddRange(lines);
            ProfileSource = $"presets (style, aggression, cohesion) with execution and skill fitted from the players of {replay.MatchId} " +
                            $"({MapCatalog.DisplayName(replay.Map)}), replay team 0 → attackers, measured with round play's perception and ExecutionTransfer.ValoReplay";
            ProfileLabel = "presets + replay players' execution";
        }

        /// <summary>The preset's decisions with another profile's measured execution (and its skill, which drives the
        /// execution that is not measured: turning, memory, hearing, decision interval).</summary>
        static Profile WithExecution(Profile preset, Profile measured, bool skill) =>
            new Profile(skill ? measured.Skill : preset.Skill, preset.Aggression, preset.Cohesion, preset.Style)
            {
                Accuracy = measured.Accuracy, Headshots = measured.Headshots, Legshots = measured.Legshots,
                Reaction = measured.Reaction, Placement = measured.Placement,
            };

        static string Describe(Profile p) =>
            $"{p.Style,-8} skill {p.Skill:0.00} aggr {p.Aggression:0.00} coh {p.Cohesion:0.00} | accuracy {p.BaseAccuracy:0.00} head {p.HeadshotShare:0.00} " +
            $"legs {p.LegShare:0.00} reaction {p.ReactionTime:0.00} s look spread {p.LookSpread:0.0}°{(p.Measured ? "" : " (from skill)")}";
    }
}
