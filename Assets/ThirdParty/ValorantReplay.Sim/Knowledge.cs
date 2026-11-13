// Knowledge.cs - what agents believe about enemies. Each agent has its own Beliefs (what it saw and heard); a team's
// TeamBlackboard is only what made it there through callouts, which arrive late or not at all depending on the
// caller's cohesion. Decisions read the better of the two, so a quiet team really plays on worse information.
// A host with its own knowledge model (Unity's per-team blackboard) replaces both with an IBeliefSource.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public enum Cue : byte { Seen = 0, Heard = 1, Callout = 2 }

    /// <summary>How RoundSim.BeliefSource combines with the agents' own knowledge (see RoundSim.BeliefSourceMode).</summary>
    public enum BeliefSourceMode : byte { Replace = 0, Supplement = 1 }

    /// <summary>What a team believes about an enemy, kept outside the simulation (RoundSim.BeliefSource). When set,
    /// every decision reads it instead of the agents' own Beliefs and the callout board: the utility brain's threats,
    /// the director's stack and rotation checks, trade targets. Perception (who sees whom, and so who can shoot whom)
    /// stays the simulation's.</summary>
    public interface IBeliefSource
    {
        /// <summary>False = the team has no idea where this enemy is. Certainty 0..1, already decayed to now.</summary>
        bool Believes(int team, int enemy, out Vector3 position, out float certainty);
    }

    public struct Sighting
    {
        public int Enemy;
        public Vector3 Position;
        public float Time;
        public float Certainty;     // at Time; decays with the reader's memory half-life
        public Cue Cue;
        public int Caller;          // Cue Callout: who made it (-1 = an outside source)

        public float CertaintyAt(float now, float halfLife) =>
            Certainty * (float)Math.Pow(0.5, Math.Max(0f, now - Time) / Math.Max(0.01f, halfLife));
    }

    public sealed class Beliefs
    {
        readonly Sighting[] last;
        readonly bool[] has;

        public Beliefs(int players)
        {
            last = new Sighting[players];
            has = new bool[players];
        }

        /// <summary>Keeps the sighting that is worth more now: fresh vision beats a stale callout, a confident old sighting
        /// beats a vague new sound only while it has not decayed below it.</summary>
        public bool Observe(Sighting s, float now, float halfLife)
        {
            if (has[s.Enemy] && last[s.Enemy].CertaintyAt(now, halfLife) > s.CertaintyAt(now, halfLife)) return false;
            last[s.Enemy] = s;
            has[s.Enemy] = true;
            return true;
        }

        public bool TryGet(int enemy, out Sighting s)
        {
            s = last[enemy];
            return has[enemy];
        }

        public void Forget(int enemy) => has[enemy] = false;
    }

    public sealed class TeamBlackboard
    {
        struct Pending { public Sighting S; public float Deliver; }

        public readonly int Team;
        public readonly Beliefs Board;
        /// <summary>Half-life used to compare sightings on the board itself (readers apply their own).</summary>
        public float HalfLife = 6f;
        public int Made { get; private set; }
        public int Dropped { get; private set; }

        readonly List<Pending> pending = new List<Pending>();

        public TeamBlackboard(int team, int players)
        {
            Team = team;
            Board = new Beliefs(players);
        }

        /// <summary>A teammate calls out a sighting. Depending on the caller's cohesion and sense it reaches the board after
        /// a delay or never, off by up to the caller's CalloutError, as a Callout with CalloutCertainty of the caller's
        /// certainty, still stamped with when it was seen.</summary>
        public void Callout(Sighting s, int callerIndex, Profile caller, float now, Random rng)
        {
            if (rng.NextDouble() < caller.CalloutDropChance) { Dropped++; return; }
            Made++;
            s.Cue = Cue.Callout;
            s.Caller = callerIndex;
            s.Certainty *= caller.CalloutCertainty;
            float err = caller.CalloutError;
            s.Position += new Vector3((float)(rng.NextDouble() * 2 - 1) * err, 0f, (float)(rng.NextDouble() * 2 - 1) * err);
            pending.Add(new Pending { S = s, Deliver = now + caller.CalloutDelay });
        }

        public void Update(float now)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].Deliver > now) continue;
                Board.Observe(pending[i].S, now, HalfLife);
                pending.RemoveAt(i);
            }
        }

        public int PendingCount => pending.Count;
    }
}
