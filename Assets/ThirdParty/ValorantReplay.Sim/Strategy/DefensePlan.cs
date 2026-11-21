// DefensePlan.cs - how the defenders play a round: who holds which site, where each player stands, when to rotate, and
// after the plant how to retake. The base class has the parts plans share (rotating onto a hit site, the retake);
// DefaultDefense is the standard setup the simulation is calibrated with.
//
// Writing a plan: subclass DefensePlan, assign sites in Begin (SimAgent.AssignedSite), say where each defender plays in
// Objective, and override Update or the retake for anything else. Like attack plans, the plan in RoundSetup.Defense or
// drawn from SimOptions.DefensePlans is a template that each round copies (Clone). TeamDirector sends defenders
// who can no longer defuse in time home to save, whatever the plan.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public abstract class DefensePlan
    {
        /// <summary>For HUDs, reports and Plans.Defense(name): the class name without "Defense".</summary>
        public virtual string Name => PlanNames.Of(GetType(), "Defense");

        // ---------------------------------------------------------------- settings (copied into every round)

        /// <summary>Believed attackers committing to one site, and none to the others, that make the other sites' defenders
        /// rotate onto it (RotateOnContact).</summary>
        public int RotateOnContact = 2;
        /// <summary>After the plant: how far from the spike towards the defender spawn defenders regroup before retaking.</summary>
        public float RegroupDepth = 0.45f;

        // ---------------------------------------------------------------- this round

        public int Rotations { get; protected set; }
        /// <summary>After the plant: the defenders have regrouped (or run out of time to) and are going in.</summary>
        public bool Retaking { get; protected set; }

        Vector3 regroupPoint;
        bool haveRegroupPoint;

        /// <summary>A copy with the same settings (shallow: collections made in Begin are the round's own).</summary>
        public DefensePlan Clone() => (DefensePlan)MemberwiseClone();

        /// <summary>Round start: give every defender its site (SimAgent.AssignedSite).</summary>
        protected internal abstract void Begin(PlanContext c);

        /// <summary>Every tick until the plant.</summary>
        protected internal virtual void Update(PlanContext c) => RotateToContact(c);

        /// <summary>Where defender a should be before the plant, and how close counts as there.</summary>
        protected internal abstract Vector3 Objective(PlanContext c, SimAgent a, out float radius);

        /// <summary>Every tick after the plant: go in once most of the living defenders are at the regroup point, or when
        /// waiting any longer would leave too little time to defuse.</summary>
        protected internal virtual void UpdateRetake(PlanContext c)
        {
            if (Retaking) return;
            if (!haveRegroupPoint)
            {
                regroupPoint = c.PointAlong(c.Sim.SpikePosition, c.Map.DefenderSpawn, RegroupDepth);
                haveRegroupPoint = true;
            }
            int alive = 0, ready = 0;
            foreach (var a in c.Members)
            {
                if (!a.Alive) continue;
                alive++;
                if (SimSpace.FlatDistance(a.Position, regroupPoint) <= 8f) ready++;
            }
            if (ready >= Math.Max(1, alive - 1) || c.Sim.SpikeTimer < c.Options.DefuseDuration + 15f)
            {
                Retaking = true;
                c.Redecide();
            }
        }

        /// <summary>Where defender a should be after the plant: the regroup point, then the spike.</summary>
        protected internal virtual Vector3 RetakeObjective(PlanContext c, SimAgent a, out float radius)
        {
            if (Retaking || !haveRegroupPoint) { radius = 3f; return c.Sim.SpikePosition; }
            radius = 8f;
            return regroupPoint;
        }

        // ---------------------------------------------------------------- building blocks

        /// <summary>Once a round: when RotateOnContact attackers are believed on their way to one site and none to the
        /// others, everyone from the other sites rotates onto it except one player per site (an anchor if there is one),
        /// who stays to watch for a fake. True when it rotated.</summary>
        protected bool RotateToContact(PlanContext c)
        {
            var sites = c.Map.Sites;
            if (Rotations > 0 || sites.Count < 2) return false;
            SimMap.Site hit = null;
            foreach (var site in sites)
            {
                int n = c.BelievedEnemiesCommitting(site);
                if (n >= RotateOnContact) { if (hit != null) return false; hit = site; }
                else if (n > 0) return false;
            }
            if (hit == null) return false;
            Rotations++;
            foreach (var site in sites)
            {
                if (site == hit) continue;
                var here = new List<SimAgent>();
                foreach (var a in c.Members) if (a.Alive && a.AssignedSite == site.Name) here.Add(a);
                var stay = here.Find(a => a.Profile.Style == PlayStyle.Anchor) ?? (here.Count > 0 ? here[0] : null);
                foreach (var a in here)
                {
                    if (a == stay) continue;
                    a.AssignedSite = hit.Name;
                    c.Redecide(a);
                }
            }
            return true;
        }

        /// <summary>Holding a's assigned site: where real defenders hold it (Map.Holds), else anywhere on it (the defender
        /// spawn when a has no site).</summary>
        protected static Vector3 HoldSite(PlanContext c, SimAgent a, out float radius)
        {
            var mine = c.Map.SiteNamed(a.AssignedSite);
            if (mine == null) { radius = 3f; return c.Map.DefenderSpawn; }
            if (c.HoldSpot(a, out var spot)) { radius = 2.5f; return spot; }
            radius = mine.Radius;
            return mine.Center;
        }
    }

    /// <summary>The standard defense: anchors first, then everyone else, dealt round-robin over the sites; aggressive
    /// non-anchors (every non-anchor on an eco) take space towards the attackers until the attack commits; rotate on
    /// contact.</summary>
    public class DefaultDefense : DefensePlan
    {
        /// <summary>Non-anchors more aggressive than this push towards the attackers until the attack commits.</summary>
        public float PushAggression = 0.7f;
        /// <summary>How far they push, along the walking path from their site to the attacker spawn.</summary>
        public float PushDepth = 0.35f;
        /// <summary>Outgunned by more than this (PlanContext.Economy below -EcoPush), every non-anchor pushes: close fights
        /// from off angles are an eco's best chance. +inf = never.</summary>
        public float EcoPush = 0.5f;

        protected internal override void Begin(PlanContext c)
        {
            var sites = c.Map.Sites;
            var defenders = new List<SimAgent>(c.Members);
            defenders.Sort((x, y) => (x.Profile.Style == PlayStyle.Anchor ? 0 : 1).CompareTo(y.Profile.Style == PlayStyle.Anchor ? 0 : 1) * 100 + x.Index.CompareTo(y.Index));
            for (int i = 0; i < defenders.Count; i++)
                defenders[i].AssignedSite = sites.Count > 0 ? sites[i % sites.Count].Name : "";
        }

        protected internal override Vector3 Objective(PlanContext c, SimAgent a, out float radius)
        {
            var mine = c.Map.SiteNamed(a.AssignedSite);
            var attackers = c.Enemy;
            bool pushes = a.Profile.Aggression > PushAggression || c.Economy < -EcoPush;
            if (mine != null && a.Profile.Style != PlayStyle.Anchor && pushes && attackers != null && !attackers.Committed)
            {
                radius = 4f;
                return c.PointAlong(mine.Center, c.Map.AttackerSpawn, PushDepth);
            }
            return HoldSite(c, a, out radius);
        }
    }
}
