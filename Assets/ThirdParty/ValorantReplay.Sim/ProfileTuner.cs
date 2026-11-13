// ProfileTuner.cs - finds the constants ProfileFitter turns measured execution into simulation parameters with.
// A measured accuracy is not a simulation parameter: the simulation's BaseAccuracy is a per-shot chance at 10 m against
// a standing target, and distance, moving and the target's movement lower what an agent actually lands. The same goes
// for reaction: turning, stopping and waiting for the next shot add to ReactionTime. So the tuner
//   1. measures the population (accuracy, hit regions, reaction) over a corpus;
//   2. simulates every recorded round from the barrier drop with fitted profiles, counts what the agents realise
//      (Measurements' definitions), and sets AccuracyTransfer = realised accuracy per unit of BaseAccuracy,
//      ReactionOffset = realised reaction minus ReactionTime, and AimErrorBase/Slope = a line through realised aim error
//      against LookSpread across players;
//   3. repeats 2, since the realised values move a little with the parameters (a fixed point).
// It works on ProfileFitter.Transfer (and the population means) as it goes; bake the printed values into an
// ExecutionTransfer preset to keep them. Tune with the SimOptions the profiles will play with: perception changes who
// notices whom and from how far, and so what a parameter turns into.
using System;
using System.Collections.Generic;
using System.Linq;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class TuneStep
    {
        public int Iteration;
        public float AccuracyTransfer, ReactionOffset, AimErrorBase, AimErrorSlope;
        public PlayerTally Recorded = new PlayerTally(), Simulated = new PlayerTally();
        /// <summary>Correlation across players of recorded and simulated accuracy, head share, reaction, aim error, kills per
        /// round.</summary>
        public float AccuracyR, HeadR, ReactionR, AimR, KillsR;
        public CalibrationReport Report;
    }

    public static class ProfileTuner
    {
        /// <summary>Measures the population of the replays into ProfileFitter's means and ProfileFitter.Transfer's (which it
        /// changes in place).</summary>
        public static PlayerTally MeasurePopulation(IList<VrpReplay> replays, IDictionary<string, SimMap> maps = null, SimOptions options = null)
        {
            var total = new PlayerTally();
            foreach (var r in replays)
            {
                var map = maps != null && maps.TryGetValue(r.Map, out var m) ? m : SimMap.FromReplays(replays.Where(x => x.Map == r.Map));
                foreach (var round in ProfileFitter.Measure(r, map, options).Rounds)
                    foreach (var t in round.Tallies.Values) total.AddShooting(t);
            }
            ProfileFitter.AccuracyMean = total.Accuracy;
            ProfileFitter.HeadHitMean = total.HeadHitRate;
            ProfileFitter.LegHitMean = total.LegHitRate;
            ProfileFitter.Transfer.ReactionMean = total.ReactionMean;
            ProfileFitter.Transfer.AimErrorMean = total.AimErrorMean;
            return total;
        }

        /// <summary>Runs the fixed point; each step's report is returned (the last one is with the final constants'
        /// predecessor, so rerun Calibration to score the result).</summary>
        public static List<TuneStep> Tune(IList<VrpReplay> replays, int iterations = 3, int runs = 40, int seed = 0, int threads = 0,
            Action<TuneStep> step = null, LearnedSource learned = LearnedSource.Holdout, SimOptions options = null)
        {
            var maps = replays.GroupBy(r => r.Map).ToDictionary(g => g.Key, g => SimMap.FromReplays(g));
            var x = ProfileFitter.Transfer = ProfileFitter.Transfer.Clone();
            MeasurePopulation(replays, maps, options);
            var steps = new List<TuneStep>();
            for (int i = 1; i <= iterations; i++)
            {
                var report = Calibration.Run(replays, new CalibrationOptions
                {
                    Runs = runs, Seed = seed, Threads = threads, Offsets = new[] { 0f }, Profiles = ProfileSource.Fitted, Learned = learned, Sim = options,
                }, maps: maps);
                var s = new TuneStep { Iteration = i, Report = report };
                double accNum = 0, accDen = 0, rtNum = 0;
                int rtDen = 0;
                foreach (var p in report.Players)
                {
                    s.Recorded.AddShooting(p.Recorded);
                    s.Simulated.AddShooting(p.Simulated);
                    accNum += p.Simulated.Hits;
                    accDen += p.Simulated.Bullets * p.Profile.BaseAccuracy;
                    rtNum += p.Simulated.ReactionSum - p.Simulated.Reactions * p.Profile.ReactionTime;
                    rtDen += p.Simulated.Reactions;
                }
                var both = report.Players.Where(p => p.Recorded.Rounds > 0 && p.Simulated.Rounds > 0).ToList();
                float R(Func<PlayerTally, bool> has, Func<PlayerTally, float> f) =>
                    CalibrationReport.Correlation(both.Where(p => has(p.Recorded) && has(p.Simulated)).Select(p => (f(p.Recorded), f(p.Simulated))).ToList());
                s.AccuracyR = R(t => t.Bullets >= 30, t => t.Accuracy);
                s.HeadR = R(t => t.Hits >= 10, t => t.HeadHitRate);
                s.ReactionR = R(t => t.Reactions >= 5, t => t.ReactionMean);
                s.AimR = R(t => t.Notices >= 5, t => t.AimErrorMean);
                s.KillsR = R(_ => true, t => t.Kills / (float)t.Rounds);
                // the constants the next step (and the caller) uses
                if (accDen > 0) x.Accuracy = (float)(accNum / accDen);
                if (rtDen > 0) x.ReactionOffset = (float)(rtNum / rtDen);
                // aim error vs LookSpread across players (least squares, players weighted by their notices)
                double w = 0, mx = 0, my = 0;
                foreach (var p in report.Players.Where(p => p.Simulated.Notices > 0))
                {
                    w += p.Simulated.Notices;
                    mx += p.Simulated.Notices * p.Profile.LookSpread;
                    my += p.Simulated.AimErrorSum;
                }
                if (w > 0)
                {
                    mx /= w; my /= w;
                    double sxy = 0, sxx = 0;
                    foreach (var p in report.Players.Where(p => p.Simulated.Notices > 0))
                    {
                        double dx = p.Profile.LookSpread - mx;
                        sxy += p.Simulated.Notices * dx * (p.Simulated.AimErrorMean - my);
                        sxx += p.Simulated.Notices * dx * dx;
                    }
                    if (sxx > 1e-6 && sxy / sxx > 0.05) x.AimErrorSlope = (float)(sxy / sxx);
                    x.AimErrorBase = (float)(my - x.AimErrorSlope * mx);
                }
                s.AccuracyTransfer = x.Accuracy;
                s.ReactionOffset = x.ReactionOffset;
                s.AimErrorBase = x.AimErrorBase;
                s.AimErrorSlope = x.AimErrorSlope;
                steps.Add(s);
                step?.Invoke(s);
            }
            return steps;
        }
    }
}
