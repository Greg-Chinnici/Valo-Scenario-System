// RoundSim.Combat.cs - gunfights: picking a target, reaction and flick, hit chance (distance, movement, aim
// streaks), damage through armor, kills.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        void Engage(SimAgent a)
        {
            if (a.Target >= 0 && (!agents[a.Target].Alive || Time - a.TargetLastSeen > 1f)) a.Target = -1;
            int best = -1;
            float bd = float.MaxValue;
            for (int j = 0; j < agents.Count; j++)
            {
                if (!sees[a.Index, j]) continue;
                float d = Vector3.Distance(a.Position, agents[j].Position);
                if (d < bd) { bd = d; best = j; }
            }
            if (best >= 0 && (a.Target < 0 || !sees[a.Index, a.Target]))
            {
                if (a.Target != best)
                {
                    // holding an angle with the crosshair already there (stationary, enemy within PreAimAngle) is faster
                    // than turning onto someone; a moving shooter also has to stop first
                    var to = agents[best].Position - a.Position;
                    float angle = Math.Abs(SimMath.DeltaYaw(a.Yaw, SimSpace.YawOf(to)));
                    // so is slicing an angle: walking round it with the crosshair already on the believed spot
                    bool slicing = a.Action == AgentAction.Peek && a.Swing == PeekStyle.Slice && a.Walking;
                    bool preAimed = ((!a.Moving && a.Action == AgentAction.Hold) || slicing) && angle <= Options.PreAimAngle;
                    // no two reactions are alike (lognormal, mean ReactionTime); finding someone who only shot at you takes longer
                    float sigma = Options.ReactionSpread;
                    float reaction = a.Profile.ReactionTime * (float)Math.Exp(sigma * SimMath.Gaussian(aimRng[a.Index]) - sigma * sigma / 2f);
                    if (onlyShotAt[a.Index, best]) reaction += Options.LocateDelay;
                    a.FireReadyAt = Time + (preAimed ? reaction * Options.PreAimReaction : reaction + angle / a.Profile.TurnSpeed)
                        + (a.Moving ? 0.08f : 0f);
                    a.EngageAngle = angle;
                    a.FlickSign = SimMath.DeltaYaw(a.Yaw, SimSpace.YawOf(to)) >= 0f ? 1f : -1f;
                }
                a.Target = best;
            }
            if (a.Target < 0 || !sees[a.Index, a.Target]) return;
            a.TargetLastSeen = Time;
            if (Time < a.FireReadyAt) return;
            var t = agents[a.Target];
            a.Yaw = SimSpace.YawOf(t.Position - a.Position);
            if (Time >= a.NextShotAt) Fire(a, t);
        }

        void Fire(SimAgent a, SimAgent t)
        {
            a.NextShotAt = Time + a.Weapon.FireInterval;
            a.LastShot = Time;
            Gunshot(a);
            notices.Shot(a, t.Index, Time);
            bool counts = a.Weapon != WeaponStats.Shotgun;
            if (counts) a.Shooting.Bullets++;
            float d = Vector3.Distance(a.Eye, t.Eye);
            float p = a.Profile.BaseAccuracy * a.Weapon.Accuracy * (float)Math.Pow(10.0 / Math.Max(Options.AccuracyNear, d), Options.AccuracyFalloff);
            if (a.Moving) p *= 0.25f;
            if (t.Moving && !t.Walking) p *= 0.85f;
            if (t.Crouching) p *= 0.9f;
            p = Math.Max(0.02f, Math.Min(0.95f, p));
            // within a spray the previous bullet decides: on target (it hit) or not. The after-miss and after-hit chances
            // keep the ratio AimStreaks and average p over a long spray; the first bullet of a spray has p.
            bool spray = a.SprayTarget == t.Index && Time - a.PrevShot <= Options.SprayGap;
            if (a.EngageAngle >= 0f)
            {
                // the first bullet at this enemy lands where the flick left the crosshair: short of the target or past it
                if (!spray)
                {
                    var flick = aimRng[a.Index];
                    float mean = Math.Min(Options.MissMax, Options.MissBase + Options.MissPerDegree * a.EngageAngle);
                    float miss = Math.Abs(SimMath.Gaussian(flick)) * mean * 1.2533f;   // half-normal with that mean
                    p = Math.Max(0.02f, Math.Min(0.95f, p * Math.Max(0.3f, 1f + Options.MissHitSlope * (Options.MissMean - miss))));
                    bool past = a.EngageAngle > 5f && flick.NextDouble() < Options.Overswing;
                    a.Yaw += (past ? 1f : -1f) * a.FlickSign * miss;
                }
                a.EngageAngle = -1f;
            }
            if (spray && Options.AimStreaks != 1f)
            {
                float afterMiss = p / (1f + p * (Options.AimStreaks - 1f));
                p = a.SprayHit ? Math.Min(0.97f, afterMiss * Options.AimStreaks) : afterMiss;
            }
            a.PrevShot = Time;
            a.SprayTarget = t.Index;
            a.SprayHit = false;
            var aim = aimRng[a.Index];
            if (aim.NextDouble() >= p) return;
            a.SprayHit = true;
            double region = aim.NextDouble();
            bool head = region < a.Profile.HeadshotShare, legs = !head && region < a.Profile.HeadshotShare + a.Profile.LegShare;
            float raw = head ? a.Weapon.Head : legs ? a.Weapon.Legs : a.Weapon.Body;
            if (counts) { a.Shooting.Hits++; if (head) a.Shooting.HeadHits++; else if (legs) a.Shooting.LegHits++; }
            hits.Add((t, a.Weapon.DamageAt(d, raw), a));
        }

        // shots of one tick land together, so two players who fire on the same tick can trade (no advantage for the
        // lower agent index)
        readonly List<(SimAgent target, float damage, SimAgent attacker)> hits = new List<(SimAgent, float, SimAgent)>();

        void ApplyHits()
        {
            foreach (var (t, dmg, by) in hits)
            {
                if (!t.Alive) continue;
                bool wasAlive = by.Alive;
                by.Alive = true;          // a shooter killed this tick still lands the shot it fired this tick
                Damage(t, dmg, by);
                if (!wasAlive) by.Alive = false;
            }
            hits.Clear();
        }

        void Damage(SimAgent t, float dmg, SimAgent attacker)
        {
            if (!t.Alive) return;
            if (t.Mode == AgentMode.Replay) Diverge(t);   // this hit is not in the recording
            float absorbed = Math.Min(t.Armor, dmg * Options.ArmorAbsorb);
            t.Armor -= absorbed;
            t.Health -= dmg - absorbed;
            t.Beliefs.Observe(new Sighting { Enemy = attacker.Index, Position = attacker.Position, Time = Time, Certainty = 0.9f, Cue = Cue.Heard }, Time, t.Profile.MemoryHalfLife);
            t.WantsDecision = true;
            Event(SimEventType.Hit, t.Index, attacker.Index);
            if (t.Health <= 0f) Kill(t, attacker, true);
        }

        void Kill(SimAgent v, SimAgent killer, bool simulated)
        {
            if (!v.Alive) return;
            v.Alive = false;
            v.Health = 0f;
            v.Moving = false;
            v.Action = AgentAction.None;
            v.Order = default;
            v.Target = -1;
            Kills.Add(new KillRecord { Time = Time, Killer = killer?.Index ?? -1, Victim = v.Index, KillerTeam = killer?.Team ?? -1, VictimTeam = v.Team, Simulated = simulated });
            Event(SimEventType.Kill, v.Index, killer?.Index ?? -1);
            if (killer != null)
            {
                killer.Kills++;
                killer.LastKillTime = Time;
                killer.LastKillPosition = v.Position;
                // the dying player's last callout: where the killer is
                boards[v.Team].Callout(new Sighting { Enemy = killer.Index, Position = killer.Position, Time = Time, Certainty = 1f, Cue = Cue.Seen }, v.Index, v.Profile, Time, commsRng[v.Index]);
            }
            lastDeath[v.Team] = new Death { Time = Time, Position = v.Position, Killer = killer?.Index ?? -1 };
            for (int j = 0; j < agents.Count; j++) { sees[v.Index, j] = false; sees[j, v.Index] = false; }
            if (SpikeState == SpikeStatus.Carried && SpikeCarrier == v.Index)
            {
                SpikeState = SpikeStatus.Dropped;
                SpikePosition = v.Position;
                SpikeCarrier = -1;
                Event(SimEventType.SpikeDropped, v.Index);
            }
            foreach (var m in agents) if (m.Alive) m.WantsDecision = true;
        }
    }
}
