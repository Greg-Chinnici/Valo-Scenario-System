using System.Numerics;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Aim streaks (hits come in runs, average accuracy kept), trust in callouts and an outside blackboard, and a
/// blackboard that supplements instead of replacing the agents' own knowledge.</summary>
public class AimTrustTests
{
    sealed class Fixed : IBeliefSource
    {
        public Func<int, int, (bool, Vector3, float)> Answer = (_, _) => (false, default, 0f);
        public bool Believes(int team, int enemy, out Vector3 position, out float certainty)
        {
            var (ok, p, c) = Answer(team, enemy);
            position = p;
            certainty = c;
            return ok;
        }
    }

    // ---------------------------------------------------------------- aim streaks

    /// <summary>An AI defender with a clear line on an attacker that cannot die (and does not shoot back), for a minute: (accuracy, share of hits whose
    /// next bullet also hit).</summary>
    static (float accuracy, float hitAfterHit) Spray(float streaks)
    {
        var setup = new List<AgentSetup>
        {
            new AgentSetup { Team = 0, Attacking = true, Position = TestWorld.Cell(27, 17), Yaw = 90f, Name = "target", Health = 1e9f, Mode = AgentMode.External },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(35, 17), Yaw = -90f, Name = "shooter", Profile = Profile.Anchor },
        };
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { TargetSite = "B" }, new SimOptions { AimStreaks = streaks, MaxDuration = 60f }, seed: 5)
        { SightTest = (_, _) => true };
        for (int i = 0; i < 1200 && !sim.Over; i++) sim.Step();
        var shooter = sim.Agents[1];
        var hits = sim.Events.Where(e => e.Type == SimEventType.Hit && e.Agent == 0).Select(e => e.Time).ToList();
        float interval = shooter.Weapon.FireInterval;
        int followed = hits.Count(t => hits.Any(u => u > t + interval * 0.5f && u < t + interval * 1.6f));
        return (shooter.Shooting.Accuracy, followed / (float)Math.Max(1, hits.Count));
    }

    [Fact]
    public void AimStreaks_ClusterHits_KeepAccuracy()
    {
        var flat = Spray(1f);
        var streaky = Spray(3.55f);
        Assert.True(streaky.hitAfterHit > flat.hitAfterHit * 1.5f, $"hit after hit: streaks {streaky.hitAfterHit:P0} vs independent {flat.hitAfterHit:P0}");
        Assert.InRange(streaky.accuracy, flat.accuracy * 0.8f, flat.accuracy * 1.2f);
    }

    // ---------------------------------------------------------------- belief sources and trust

    // attacker 0 (External: stands still, makes no sound) on B, defender 1 facing it 20 m away, defender 2 nearby; nobody
    // sees anybody unless see says so
    static RoundSim Trio(IBeliefSource source, BeliefSourceMode mode, Profile reader, Func<RoundSim, Vector3, Vector3, bool> see = null)
    {
        var setup = new List<AgentSetup>
        {
            new AgentSetup { Team = 0, Attacking = true, Position = TestWorld.Cell(27, 17), Yaw = 90f, Name = "atk", Mode = AgentMode.External, Health = 1e9f },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(35, 17), Yaw = -90f, Name = "reader", Profile = reader },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(33, 16), Yaw = -90f, Name = "caller", Profile = new Profile(1f, 0.5f, 1f) },
        };
        RoundSim sim = null;
        sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { TargetSite = "B" }, new SimOptions { GlanceRate = 0f }, seed: 3)
        {
            SightTest = (a, b) => see != null && see(sim, a, b),
            BeliefSource = source,
            BeliefSourceMode = mode,
        };
        return sim;
    }

    static float Hold(RoundSim sim) => sim.Agents[1].Scores[0];   // rises with the believed threat

    [Fact]
    public void Supplement_KeepsTheAgentsOwnMemory()
    {
        RoundSim Run(BeliefSourceMode mode)
        {
            var sim = Trio(new Fixed(), mode, Profile.Default);
            var atk = sim.Agents[0];
            sim.Agents[1].Beliefs.Observe(new Sighting { Enemy = 0, Position = atk.Position, Time = 0f, Certainty = 1f, Cue = Cue.Seen }, 0f, 7f);
            sim.Step();
            return sim;
        }
        Assert.Equal(0f, Run(BeliefSourceMode.Replace).Agents[1].Scores[2]);   // the blackboard knows nothing: no peek
        Assert.True(Run(BeliefSourceMode.Supplement).Agents[1].Scores[2] > 0f); // its own sighting still counts
    }

    [Fact]
    public void Trust_ScalesWhatTheBlackboardSays()
    {
        float HoldWith(float trust)
        {
            var src = new Fixed();
            var sim = Trio(src, BeliefSourceMode.Supplement, new Profile { Trust = trust });
            var at = sim.Agents[0].Position;
            src.Answer = (team, enemy) => team == 1 && enemy == 0 ? (true, at, 0.4f) : (false, default, 0f);
            sim.Step();
            return Hold(sim);
        }
        Assert.True(HoldWith(1f) > HoldWith(0.5f));
        Assert.True(HoldWith(0.5f) > HoldWith(0f));
    }

    [Fact]
    public void Trust_PerTeammate()
    {
        // the caller sees the attacker in the first perceptions and calls it out; the reader only knows through the callout
        float HoldAfterCallout(float? trustInCaller)
        {
            var sim = Trio(null, BeliefSourceMode.Replace, Profile.Default, (s, a, b) =>
                s.Time < 0.3f && (Near(a, s.Agents[2].Eye) || Near(b, s.Agents[2].Eye)));
            if (trustInCaller != null) sim.SetTrust(1, 2, trustInCaller.Value);
            for (int i = 0; i < 30; i++) sim.Step();
            Assert.False(sim.Agents[1].Beliefs.TryGet(0, out _));   // never saw or heard the attacker itself
            return Hold(sim);
        }
        float distrust = HoldAfterCallout(0f), trust = HoldAfterCallout(1f), profile = HoldAfterCallout(null);
        Assert.True(trust > profile && profile > distrust, $"hold: trusts the caller {trust:0.000}, profile {profile:0.000}, distrusts {distrust:0.000}");
        Assert.Equal(profile, HoldAfterCallout(float.NaN));        // NaN goes back to the profile's trust
    }

    static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.01f;
}
