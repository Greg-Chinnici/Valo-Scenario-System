using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Shooting measured from recordings and counted in simulations, and measured profiles.</summary>
public class MeasurementTests
{
    // attacker 0 and defender 1 face each other down the top lane (0 looks +x at 1, 1 looks -x at 0), 30 m apart
    static VrpReplay Duel()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) }, new List<Kill>());
        var eye0 = TestWorld.Cell(2, 1) + new System.Numerics.Vector3(0f, 0.65f, 0f);
        var eye1 = TestWorld.Cell(14, 1) + new System.Numerics.Vector3(0f, 0.65f, 0f);
        Shot S(float time, byte player, System.Numerics.Vector3 from, (float, float, float) dir, string weapon = "AssaultRifle_AK", byte pellets = 1) =>
            new Shot { Time = time, Player = player, Weapon = weapon, X = from.X, Y = from.Y, Z = from.Z, Projectiles = pellets, Directions = { dir } };
        r.Shots.Add(S(0.3f, 1, eye1, (0f, 0f, 1f)));                 // 1 fires sideways first: no reaction for 1
        r.Shots.Add(S(0.4f, 0, eye0, (1f, 0f, 0f)));                 // 0 answers at 0.4 s, straight at 1
        r.Shots.Add(S(0.5f, 0, eye0, (1f, 0f, 0f)));
        r.Shots.Add(S(0.6f, 0, eye0, (1f, 0f, 0f)));
        r.Shots.Add(S(0.7f, 1, eye1, (-1f, 0f, 0f), "PumpShotgun", 15));   // pellets: not counted for accuracy
        r.Damage.Add(new DamageEvent { Time = 0.41f, Attacker = 0, Victim = 1, Damage = 40, Weapon = "AssaultRifle_AK", Region = HitRegion.Body });
        r.Damage.Add(new DamageEvent { Time = 0.51f, Attacker = 0, Victim = 1, Damage = 60, Weapon = "AssaultRifle_AK", Region = HitRegion.Head });
        r.Damage.Add(new DamageEvent { Time = 0.71f, Attacker = 1, Victim = 0, Damage = 90, Weapon = "PumpShotgun", Region = HitRegion.Body });
        return r;
    }

    [Fact]
    public void Recorded_AccuracyRegionsAndReaction()
    {
        var into = new Dictionary<int, PlayerTally>();
        Measurements.AddShooting(Duel(), TestWorld.Map, 0f, 5f, into);
        var a = into[0];
        Assert.Equal((3, 2, 1, 0), (a.Bullets, a.Hits, a.HeadHits, a.LegHits));
        Assert.Equal(1, a.Notices);                                  // in view from the start, once
        Assert.Equal(1, a.Reactions);
        Assert.Equal(0.4f, a.ReactionMean, 2);
        Assert.Equal(0f, a.AimErrorMean, 1);                         // looking straight at 1
        var b = into[1];
        Assert.Equal((1, 0), (b.Bullets, b.Hits));                   // the sideways rifle shot; the shotgun is left out
        Assert.Equal((1, 0), (b.Notices, b.Reactions));              // its first shot went elsewhere
    }

    [Fact]
    public void Recorded_WindowLimitsCounts()
    {
        var into = new Dictionary<int, PlayerTally>();
        Measurements.AddShooting(Duel(), TestWorld.Map, 0.45f, 5f, into);
        Assert.Equal((2, 1), (into[0].Bullets, into[0].Hits));
    }

    [Fact]
    public void Simulated_CountsWithTheSameDefinitions()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) }, new List<Kill>());
        var bullets = 0; var notices = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            var res = RoundSim.FromReplay(r, TestWorld.Map, 0f, _ => Profile.Default, followRecording: false, seed: seed).RunToEnd();
            Assert.Equal(2, res.Shooting.Count);
            foreach (var t in res.Shooting)
            {
                Assert.InRange(t.Hits, 0, t.Bullets);
                Assert.InRange(t.HeadHits + t.LegHits, 0, t.Hits);
                Assert.InRange(t.Reactions, 0, t.Notices);
                Assert.InRange(t.ReactionMean, 0f, Measurements.ReactionWindow);
                bullets += t.Bullets; notices += t.Notices;
            }
        }
        Assert.True(bullets > 0 && notices > 0);
    }

    [Fact]
    public void Simulated_AccuracyFollowsTheParameter()
    {
        BatchResult Run(float acc) => SimBatch.Run(i => TestWorld.Round(_ => new Profile { Accuracy = acc }, _ => new Profile { Accuracy = acc }, i), 150, threads: 0);
        var lo = Run(0.2f).Combined(Enumerable.Range(0, 10));
        var hi = Run(0.6f).Combined(Enumerable.Range(0, 10));
        Assert.True(hi.Accuracy > lo.Accuracy * 2f, $"acc 0.2 -> {lo.Accuracy:P0}, 0.6 -> {hi.Accuracy:P0}");
        var hs = SimBatch.Run(i => TestWorld.Round(_ => new Profile { Headshots = 0.5f, Legshots = 0f }, _ => new Profile { Headshots = 0.5f, Legshots = 0f }, i), 100)
            .Combined(Enumerable.Range(0, 10));
        Assert.InRange(hs.HeadHitRate, 0.42f, 0.58f);
        Assert.Equal(0, hs.LegHits);
    }

    [Fact]
    public void Simulated_PlacementSpreadsTheAim()
    {
        float AimError(float spread) => SimBatch.Run(i => TestWorld.Round(_ => new Profile { Placement = spread }, _ => new Profile { Placement = spread }, i), 150, threads: 0)
            .Combined(Enumerable.Range(0, 10)).AimErrorMean;
        float tight = AimError(0f), loose = AimError(30f);
        Assert.True(loose > tight + 5f, $"spread 0 -> {tight:0.0} deg, 30 -> {loose:0.0} deg");
    }

    [Fact]
    public void Profile_MeasuredOverridesSkill()
    {
        var p = new Profile(0.9f, 0.5f, 0.5f) { Accuracy = 0.25f, Reaction = 0.4f };
        Assert.Equal(0.25f, p.BaseAccuracy);
        Assert.Equal(0.4f, p.ReactionTime);
        Assert.Equal(Profile.Lerp(0.12f, 0.35f, 0.9f), p.HeadshotShare);   // not measured: from skill
        var q = p.With(skill: 0.1f);
        Assert.Equal((0.25f, 0.4f), (q.BaseAccuracy, q.ReactionTime));
        Assert.False(p.Unmeasured().Measured);
        Assert.Equal(Profile.Lerp(0.30f, 0.70f, 0.9f), p.Unmeasured().BaseAccuracy);
    }

    [Fact]
    public void Fit_ShrinksTowardsThePopulation()
    {
        var none = ProfileFitter.Fit(Duel(), _ => false, TestWorld.Map)[0].Profile;   // no data: the population
        Assert.Equal(ProfileFitter.HeadHitMean, none.HeadshotShare, 3);
        var x = ProfileFitter.Transfer;
        Assert.Equal(ProfileFitter.AccuracyMean / x.Accuracy, none.BaseAccuracy, 3);
        Assert.Equal(x.ReactionMean - x.ReactionOffset, none.ReactionTime, 3);
        Assert.Equal(x.PlacementFor(x.AimErrorMean), none.LookSpread, 3);
        var some = ProfileFitter.Fit(Duel(), map: TestWorld.Map)[0].Profile;            // 2 of 3 bullets hit: above, but shrunk
        Assert.InRange(some.BaseAccuracy, none.BaseAccuracy + 0.001f, 0.67f / x.Accuracy);
        var other = new ExecutionTransfer { Accuracy = x.Accuracy * 2f, ReactionMean = x.ReactionMean, AimErrorMean = x.AimErrorMean, AimErrorBase = x.AimErrorBase, AimErrorSlope = x.AimErrorSlope };
        Assert.Equal(none.BaseAccuracy / 2f, ProfileFitter.Fit(Duel(), _ => false, TestWorld.Map, transfer: other)[0].Profile.BaseAccuracy, 3);   // a transfer can be passed
    }
}
