// AttackPlan.cs - how the attackers play a round before the plant: which site, when to go onto it, and where each
// player waits until then. The base class is the standard attack, which the built-in plans only retime: wait on the
// way to the target site (lurkers on the way to the next one), go onto the site at ExecuteAt, and rotate once if the
// site turns out to be stacked.
//
// Writing a plan: subclass AttackPlan, say when it executes (ExecuteTime), and override Begin, Update and Objective for
// whatever it does differently (FakeAttack, LateCallAttack). The plan in RoundSetup.Attack, or drawn from
// SimOptions.AttackPlans, is a template: each round plays a copy of it (Clone), so put settings in fields and
// create any per-round collections in Begin. TeamDirector deals with the spike for every plan; Plans lists the built-in
// ones by name.
using System;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public abstract class AttackPlan
    {
        /// <summary>How far along the walking path from the attacker spawn to a site players wait before an execute.</summary>
        public const float StagingDepth = 0.55f;

        /// <summary>For HUDs, reports and Plans.Attack(name): the class name without "Attack".</summary>
        public virtual string Name => PlanNames.Of(GetType(), "Attack");

        // ---------------------------------------------------------------- settings (copied into every round)

        /// <summary>Execute at this much round time left instead of when the plan would (NaN = the plan's own timing).
        /// The plan's own timing is still drawn, so a round with and without this setting keeps the same dice.</summary>
        public float FixedExecuteAt = float.NaN;
        /// <summary>Believed defenders on the target site that make the attackers rotate to the site they believe least
        /// defended (once, with RotateUntil or more round time left).</summary>
        public int StackThreshold = 3;
        public float RotateUntil = 25f;
        /// <summary>Outgunned by more than this (PlanContext.Economy below -EcoRush, e.g. pistols against rifles), the team
        /// goes onto its site from the start: on an eco, waiting only gives the better guns time. +inf = never.</summary>
        public float EcoRush = 0.5f;

        // ---------------------------------------------------------------- this round

        /// <summary>The site the team will plant on ("" = not decided yet).</summary>
        public string TargetSite { get; protected set; } = "";
        /// <summary>Round time left at which the team goes onto its site (+inf = from the start).</summary>
        public float ExecuteAt { get; protected set; }
        public bool Executing { get; protected set; }
        public int Rotations { get; protected set; }
        /// <summary>The attack has shown where it is going, for real or not: defenders stop taking space (DefaultDefense).</summary>
        public virtual bool Committed => Executing;

        /// <summary>A copy with the same settings (shallow: collections made in Begin are the round's own).</summary>
        public AttackPlan Clone() => (AttackPlan)MemberwiseClone();

        /// <summary>When the plan executes, as round time left. A random timing is drawn here (from c.Random).</summary>
        protected abstract float ExecuteTime(PlanContext c);

        /// <summary>The target site when the round setup names none: a random one.</summary>
        protected virtual string ChooseSite(PlanContext c)
        {
            var sites = c.Map.Sites;
            return sites.Count > 0 ? sites[c.Random.Next(sites.Count)].Name : "";
        }

        /// <summary>Round start. site: RoundSetup.TargetSite (null = the plan chooses).</summary>
        protected internal virtual void Begin(PlanContext c, string site)
        {
            float own = ExecuteTime(c);
            if (c.Economy < -EcoRush) own = float.PositiveInfinity;
            ExecuteAt = float.IsNaN(FixedExecuteAt) ? own : FixedExecuteAt;
            TargetSite = site ?? ChooseSite(c);
            Executing = c.Remaining <= ExecuteAt;
        }

        /// <summary>Every tick until the plant.</summary>
        protected internal virtual void Update(PlanContext c)
        {
            if (!Executing && c.Remaining <= ExecuteAt) Executing = true;
            RotateIfStacked(c);
        }

        /// <summary>Where agent a should be before the plant, and how close counts as there. (TeamDirector sends someone
        /// for a dropped spike and handles the round after the plant itself.)</summary>
        protected internal virtual Vector3 Objective(PlanContext c, SimAgent a, out float radius)
        {
            string site = TargetSite;
            if (a.Profile.Style == PlayStyle.Lurker && !Executing && c.Map.Sites.Count > 1) site = c.SiteAfter(TargetSite).Name;
            if (Executing && site == TargetSite) return OnSite(c, site, out radius);
            return OnTheWay(c, site, StagingDepth, out radius);
        }

        // ---------------------------------------------------------------- building blocks

        /// <summary>The standard timing: when attacks on this map really go in (TempoModel, drawn per round), else at 55 s left.</summary>
        protected static float MapTiming(PlanContext c) =>
            c.Map.Tempo != null && c.Map.Tempo.Known ? c.Options.RoundTime - c.Map.Tempo.DrawExecute(c.Random) : 55f;

        /// <summary>Onto a site: its centre, anywhere on it counting as there (the defender spawn if there is no such site).</summary>
        protected static Vector3 OnSite(PlanContext c, string site, out float radius)
        {
            var s = c.Map.SiteNamed(site);
            if (s == null) { radius = 3f; return c.Map.DefenderSpawn; }
            radius = s.Radius;
            return s.Center;
        }

        /// <summary>Waiting on the way to a site, depth (0..1) of the walking path from the attacker spawn.</summary>
        protected static Vector3 OnTheWay(PlanContext c, string site, float depth, out float radius)
        {
            var s = c.Map.SiteNamed(site);
            if (s == null) { radius = 3f; return c.Map.DefenderSpawn; }
            radius = 4f;
            return c.PointAlong(c.Map.AttackerSpawn, s.Center, depth);
        }

        /// <summary>Once a round, with RotateUntil left: when StackThreshold defenders are believed on the target site,
        /// switch to the site believed least defended. True when it rotated.</summary>
        protected bool RotateIfStacked(PlanContext c)
        {
            if (Rotations > 0 || c.Remaining < RotateUntil || c.Map.Sites.Count < 2) return false;
            var site = c.Map.SiteNamed(TargetSite);
            if (site == null || c.BelievedEnemiesNear(site.Center, site.Radius * 2f) < StackThreshold) return false;
            TargetSite = LeastDefended(c, TargetSite);
            Rotations++;
            c.Redecide();
            return true;
        }

        /// <summary>The site with the fewest believed defenders, other than except (the first such in map order).</summary>
        protected static string LeastDefended(PlanContext c, string except = null)
        {
            string best = except ?? "";
            int fewest = int.MaxValue;
            foreach (var s in c.Map.Sites)
            {
                if (s.Name == except) continue;
                int n = c.BelievedEnemiesNear(s.Center, s.Radius * 2f);
                if (n < fewest) { fewest = n; best = s.Name; }
            }
            return best;
        }
    }

    /// <summary>The standard attack, executing when attacks on this map really do (TempoModel), else at 55 s left. The
    /// simulation is calibrated with it.</summary>
    public class DefaultAttack : AttackPlan
    {
        protected override float ExecuteTime(PlanContext c) => MapTiming(c);
    }

    /// <summary>Straight onto the site from the start.</summary>
    public class RushAttack : AttackPlan
    {
        protected override float ExecuteTime(PlanContext c) => float.PositiveInfinity;
    }

    /// <summary>Take space and wait; execute with 30 s left.</summary>
    public class SlowAttack : AttackPlan
    {
        protected override float ExecuteTime(PlanContext c) => 30f;
    }

    static class PlanNames
    {
        public static string Of(Type t, string suffix) =>
            t.Name.Length > suffix.Length && t.Name.EndsWith(suffix, StringComparison.Ordinal) ? t.Name.Substring(0, t.Name.Length - suffix.Length) : t.Name;
    }
}
