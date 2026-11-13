using System.Collections.Generic;
using UnityEngine;

namespace ValoReplay.Knowledge
{
    /// <summary>How a piece of knowledge was learned.</summary>
    public enum IntelSource : byte
    {
        None,
        Seen,       // in an ally's view cone with a clear line of sight
        Revealed,   // an ally's recon ability (Sova dart, Fade prowler, Cypher trapwire, ...)
        Heard,      // a sound within its falloff range of an ally
        HitBy,      // it damaged or debuffed an ally
        KillFeed,   // public: the kill feed
        LastRound,  // known last round, and they survived it (survivors keep their guns)
    }

    public enum CueKind : byte { Footstep, Landing, Gunshot, Reload, Ability, Plant, Defuse, Damage }

    /// <summary>A sound (or damage indicator) one team picked up. Position is blurred by distance: the true source is
    /// somewhere within Radius of it.</summary>
    public struct SoundCue
    {
        public float Time;
        public CueKind Kind;
        public int Player;              // who made it
        public int Listener;            // ally who heard it best
        public Vector3 Position;
        public float Radius, Certainty, Distance;
        public string Detail;           // gun class for gunshots/reloads, slot for abilities
    }

    /// <summary>An enemy ability use the team knows about (one per recorded cast, best source kept).</summary>
    public sealed class KnownCast
    {
        public float Time;
        public int Player;
        public string Slot;             // "C", "Q", "E", "X"; "" = could not tell which
        public IntelSource How;
        public float Certainty;
        public int CastIndex = -1;      // VrpReplay.AbilityCasts index, -1 = no recorded cast matched
    }

    /// <summary>An enemy utility object an ally has seen.</summary>
    public sealed class UtilIntel
    {
        public int Entity;
        public int Owner;
        public string Slot = "";
        public Vector3 Position;
        public float FirstSeen, LastSeen;
        /// <summary>Seen disappearing: it is no longer there.</summary>
        public bool Gone;
    }

    /// <summary>Everything one team believes about one enemy.</summary>
    public sealed class EnemyIntel
    {
        public readonly int Player;
        public EnemyIntel(int player) { Player = player; }

        public bool Dead;
        public float DeathTime;
        /// <summary>In an ally's sight at the last sensor update.</summary>
        public bool Visible;
        public int Spotter = -1;

        public bool HasPosition;
        public Vector3 Position;
        public float PositionTime, PositionRadius, PositionCertainty;
        public IntelSource PositionSource;
        public CueKind PositionCue;     // when PositionSource is Heard / HitBy

        /// <summary>Guns this enemy is known to have this round, most recent last.</summary>
        public readonly List<string> Guns = new List<string>();
        public string Gun = "";         // last gun seen or heard
        public float GunTime;
        public IntelSource GunSource;

        public readonly List<KnownCast> Casts = new List<KnownCast>();

        /// <summary>Credits at the start of the round (public), -1 unknown, and the buy they point to.</summary>
        public int RoundStartCredits = -1;
        public BuyType Buy;
        /// <summary>Survived the previous round of this half, so kept their guns.</summary>
        public bool Survived;
        /// <summary>Ult points now (public on the scoreboard), -1 unknown; cost 0 = unknown (never seen used).</summary>
        public int UltPoints = -1, UltCost;
        public bool UltReady => UltCost > 0 && UltPoints >= UltCost;
        /// <summary>Most charges of C, Q, E seen for this agent (their kit), for "used of kit".</summary>
        public readonly int[] Kit = new int[3];

        /// <summary>Probability of being in each <see cref="AreaModel"/> area (sums to 1 while alive); null without
        /// an area model.</summary>
        public float[] Areas;

        /// <summary>Most likely area, or -1.</summary>
        public int MostLikelyArea(out float probability)
        {
            probability = 0f;
            if (Areas == null || Dead) return -1;
            int best = -1;
            for (int i = 0; i < Areas.Length; i++)
                if (Areas[i] > probability) { probability = Areas[i]; best = i; }
            return best;
        }

        /// <summary>Position certainty at a time: halves every <see cref="KnowledgeSettings.certaintyHalfLife"/>
        /// seconds since the last sighting or sound.</summary>
        public float CertaintyAt(float time, KnowledgeSettings s)
        {
            if (!HasPosition || Dead) return 0f;
            float age = Mathf.Max(0f, time - PositionTime);
            return PositionCertainty * Mathf.Pow(0.5f, age / Mathf.Max(0.01f, s.certaintyHalfLife));
        }

        /// <summary>Where the enemy could be by now: the observation's blur plus how far they could have moved.</summary>
        public float RadiusAt(float time, KnowledgeSettings s)
        {
            float age = Mathf.Max(0f, time - PositionTime);
            return Mathf.Min(s.maxRadius, PositionRadius + s.enemyMoveSpeed * age);
        }

        public int CastCount(string slot)
        {
            int n = 0;
            foreach (var c in Casts) if (c.Slot == slot) n++;
            return n;
        }

        internal void Reset()
        {
            Dead = Visible = HasPosition = false;
            Spotter = -1;
            PositionSource = IntelSource.None;
            Guns.Clear();
            Gun = "";
            GunSource = IntelSource.None;
            Casts.Clear();
            if (Areas != null) System.Array.Clear(Areas, 0, Areas.Length);
            RoundStartCredits = -1;
            Buy = BuyType.Unknown;
            Survived = false;
        }
    }

    /// <summary>
    /// One team's shared knowledge of the other team within a round: last known positions, sounds heard, enemy guns,
    /// utility seen and abilities known to be used. Filled only through the team's senses by <see cref="KnowledgeSim"/>.
    /// </summary>
    public sealed class TeamBlackboard
    {
        public const int MaxSounds = 256;

        public readonly int Team;
        /// <summary>Indexed by player; null for allies.</summary>
        public readonly EnemyIntel[] Enemies;
        public readonly List<SoundCue> Sounds = new List<SoundCue>();
        public readonly List<UtilIntel> Utility = new List<UtilIntel>();
        /// <summary>Expected number of living enemies per area (sum of their area probabilities); null without areas.</summary>
        public float[] Expected;
        /// <summary>The enemy team's buy, from their average round-start credits.</summary>
        public BuyType EnemyBuy;
        public int EnemyAverageCredits = -1;

        public TeamBlackboard(int team, IReadOnlyList<int> playerTeams)
        {
            Team = team;
            Enemies = new EnemyIntel[playerTeams.Count];
            for (int i = 0; i < Enemies.Length; i++)
                if (playerTeams[i] != team) Enemies[i] = new EnemyIntel(i);
        }

        public IEnumerable<EnemyIntel> EachEnemy()
        {
            foreach (var e in Enemies) if (e != null) yield return e;
        }

        internal void Reset()
        {
            foreach (var e in EachEnemy()) e.Reset();
            Sounds.Clear();
            Utility.Clear();
            if (Expected != null) System.Array.Clear(Expected, 0, Expected.Length);
            EnemyBuy = BuyType.Unknown;
            EnemyAverageCredits = -1;
        }

        internal void AddSound(SoundCue cue)
        {
            if (Sounds.Count >= MaxSounds) Sounds.RemoveRange(0, MaxSounds / 4);
            Sounds.Add(cue);
        }
    }
}
