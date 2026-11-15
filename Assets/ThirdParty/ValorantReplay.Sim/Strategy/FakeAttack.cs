// FakeAttack.cs - show on one site, hit the other. A few attackers (Fakers) push deep towards the fake site when the
// attack would go in, while the rest wait short of the real one, far enough back that defenders do not read them as
// committing there. Once the attackers believe defenders have rotated onto the fake, the fakers are down, SwitchAfter
// has passed or time runs short, everyone executes on the real site.
//
// It works through the defenders' own reads: DefaultDefense rotates when RotateOnContact attackers are believed to
// commit to one site and none to the others, so the fake needs at least that many fakers seen or heard, and nobody
// past 40% of the way to the real site (HoldDepth).
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public class FakeAttack : AttackPlan
    {
        /// <summary>The site to fake (null = drawn from the sites other than the target). This round's fake site once it
        /// has begun ("" = nothing to fake: a one-site map, where the plan plays as DefaultAttack).</summary>
        public string FakeSite;
        /// <summary>How many attackers show on the fake: lurkers first, then entries, never the spike carrier.</summary>
        public int Fakers = 2;
        /// <summary>How far along the walking path to the fake site the fakers go: where they are seen and heard committing.</summary>
        public float FakeDepth = 0.8f;
        /// <summary>How far along the path to the real site the others wait until the switch.</summary>
        public float HoldDepth = 0.35f;
        /// <summary>The switch comes this many seconds after the fake started at the latest...</summary>
        public float SwitchAfter = 12f;
        /// <summary>...or once the attackers believe this many more defenders near the fake site than when it started...</summary>
        public int Drawn = 2;
        /// <summary>...or at this much round time left.</summary>
        public float SwitchBy = 30f;

        /// <summary>The fakers are showing on the fake site; the real execute has not started.</summary>
        public bool Faking { get; private set; }
        public override bool Committed => Faking || Executing;
        /// <summary>The agents chosen to fake this round.</summary>
        public IReadOnlyList<int> FakerAgents => fakers;

        List<int> fakers;
        float fakeStarted;
        int believedAtStart;

        protected override float ExecuteTime(PlanContext c) => MapTiming(c);

        protected internal override void Begin(PlanContext c, string site)
        {
            base.Begin(c, site);
            if (FakeSite == null || FakeSite == TargetSite || c.Map.SiteNamed(FakeSite) == null) FakeSite = OtherSite(c);
            fakers = PickFakers(c);
            if (FakeSite != "") Executing = false;    // a late start fakes straight away instead (Update)
        }

        protected internal override void Update(PlanContext c)
        {
            if (FakeSite == "" || Executing) { base.Update(c); return; }
            var fake = c.Map.SiteNamed(FakeSite);
            int believed = c.BelievedEnemiesNear(fake.Center, fake.Radius * 2f);
            if (!Faking)
            {
                if (c.Remaining > ExecuteAt) return;
                Faking = true;
                fakeStarted = c.Remaining;
                believedAtStart = believed;
                c.Redecide();
                return;
            }
            bool drawn = believed >= believedAtStart + Drawn;
            bool fakersDown = !fakers.Exists(i => c.Sim.Agents[i].Alive);
            if (drawn || fakersDown || fakeStarted - c.Remaining >= SwitchAfter || c.Remaining <= SwitchBy)
            {
                Faking = false;
                Executing = true;
                c.Redecide();
            }
        }

        protected internal override Vector3 Objective(PlanContext c, SimAgent a, out float radius)
        {
            if (FakeSite == "" || Executing) return base.Objective(c, a, out radius);
            if (fakers.Contains(a.Index) && !c.CarriesSpike(a)) return OnTheWay(c, FakeSite, Faking ? FakeDepth : StagingDepth, out radius);
            return OnTheWay(c, TargetSite, HoldDepth, out radius);
        }

        string OtherSite(PlanContext c)
        {
            var others = c.Map.Sites.FindAll(s => s.Name != TargetSite);
            return others.Count > 0 ? others[c.Random.Next(others.Count)].Name : "";
        }

        List<int> PickFakers(PlanContext c)
        {
            var candidates = new List<SimAgent>();
            foreach (var a in c.Members) if (a.Alive && !c.CarriesSpike(a)) candidates.Add(a);
            candidates.Sort((x, y) => Rank(x) != Rank(y) ? Rank(x).CompareTo(Rank(y)) : x.Index.CompareTo(y.Index));
            var picked = new List<int>();
            for (int i = 0; i < candidates.Count && picked.Count < Fakers; i++) picked.Add(candidates[i].Index);
            return picked;
        }

        static int Rank(SimAgent a) => a.Profile.Style switch
        {
            PlayStyle.Lurker => 0,
            PlayStyle.Entry => 1,
            PlayStyle.Balanced => 2,
            PlayStyle.Support => 3,
            _ => 4,
        };
    }
}
