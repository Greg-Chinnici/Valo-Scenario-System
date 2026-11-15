// LateCallAttack.cs - spread out, then call it. Attackers take space towards every site, dealt round-robin the way
// defenders split, and when the attack would go in they all hit the site they believe is defended least (the one the
// team is closer to on a tie). With a site named in the round setup the call is made already and only the spread differs.
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public class LateCallAttack : AttackPlan
    {
        Dictionary<int, string> lanes;

        protected override float ExecuteTime(PlanContext c) => MapTiming(c);

        protected override string ChooseSite(PlanContext c) => "";     // decided at the call

        protected internal override void Begin(PlanContext c, string site)
        {
            base.Begin(c, site);
            lanes = new Dictionary<int, string>();
            var sites = c.Map.Sites;
            int k = 0;
            foreach (var a in c.Members)
                if (a.Alive && sites.Count > 0) lanes[a.Index] = sites[k++ % sites.Count].Name;
        }

        protected internal override void Update(PlanContext c)
        {
            if (!Executing && c.Remaining <= ExecuteAt) Executing = true;
            if (Executing && TargetSite == "" && c.Map.Sites.Count > 0)
            {
                TargetSite = Call(c);
                c.Redecide();
            }
            RotateIfStacked(c);
        }

        protected internal override Vector3 Objective(PlanContext c, SimAgent a, out float radius)
        {
            if (Executing && TargetSite != "") return OnSite(c, TargetSite, out radius);
            return OnTheWay(c, lanes.TryGetValue(a.Index, out var lane) ? lane : TargetSite, StagingDepth, out radius);
        }

        static string Call(PlanContext c)
        {
            string best = "";
            int fewest = int.MaxValue;
            float nearest = float.MaxValue;
            foreach (var s in c.Map.Sites)
            {
                int n = c.BelievedEnemiesNear(s.Center, s.Radius * 2f);
                float d = 0f;
                foreach (var a in c.Members) if (a.Alive) d += SimSpace.FlatDistance(a.Position, s.Center);
                if (n < fewest || (n == fewest && d < nearest)) { fewest = n; nearest = d; best = s.Name; }
            }
            return best;
        }
    }
}
