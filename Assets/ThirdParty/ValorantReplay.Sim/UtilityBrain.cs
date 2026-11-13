// UtilityBrain.cs - per-agent decisions. RoundSim boils the world down to a Situation; the brain scores five actions
// from it and the agent's Profile, and picks the best. Pure functions, so the effect of each knob can be tested in
// isolation and the scores can be shown in Unity next to each agent.
using System;

namespace ValorantReplay.Sim
{
    public enum AgentAction : byte { None = 0, Hold = 1, Advance = 2, Peek = 3, FallBack = 4, Trade = 5, Plant = 6, Defuse = 7 }

    /// <summary>What an agent knows when it decides. Distances in metres; enemy facts come from its beliefs plus the team
    /// board, never from the true positions.</summary>
    public struct Situation
    {
        public bool Attacking;
        public bool SpikePlanted;
        public bool CarriesSpike;
        public float TimePressure;      // 0 = round just started, 1 = out of time (attackers before the plant)
        public float Urgency;           // 0 = time to spare, 1 = must go now (time left vs time to reach and finish the objective)
        public float Health;            // 0..1, health and armor
        public float NearestThreat;     // distance to the most pressing believed enemy; +inf if none
        public float ThreatCertainty;   // that enemy's certainty now
        public int AlliesNear;          // living teammates within 15 m
        public int EnemiesNear;         // believed enemies within 20 m with certainty > 0.3
        public int EnemiesVisible;
        public bool TradeAvailable;     // a teammate died recently within TradeRange
        public float AheadOfTeam;       // how much closer to the objective this agent is than the team's average
        public float ObjectiveDistance; // walking distance to the director's objective for this agent
        public bool AtObjective;
    }

    public static class UtilityBrain
    {
        public static readonly AgentAction[] Choices = { AgentAction.Hold, AgentAction.Advance, AgentAction.Peek, AgentAction.FallBack, AgentAction.Trade };

        /// <summary>Scores Choices (same order) into scores. Higher is better; 0 = not an option.</summary>
        /// <summary>Aggression with the clock: urgency pushes it towards 1, so even passive players commit when they must.</summary>
        public static float EffectiveAggression(in Situation s, Profile p) => p.Aggression + (1f - p.Aggression) * s.Urgency;

        public static void Score(in Situation s, Profile p, float[] scores)
        {
            float a = EffectiveAggression(s, p), c = p.Cohesion;
            float threat = s.ThreatCertainty * Profile.Clamp01(1f - s.NearestThreat / 35f);   // 0 = nothing, 1 = certain and close
            float odds = Math.Max(-1f, Math.Min(1f, (s.AlliesNear + 1 - s.EnemiesNear) / 3f));  // > 0 = numbers advantage
            bool holdingSide = s.Attacking ? s.SpikePlanted : !s.SpikePlanted;                // the side that waits for the other

            // Hold: wait on an angle. Passive players, the waiting side once in position, anchors.
            float hold = 0.25f + 0.35f * (1f - a) + 0.25f * threat * (1f - a);
            if (holdingSide && s.AtObjective) hold += 0.3f;
            hold -= 0.45f * Profile.Clamp01(s.ObjectiveDistance / 25f) * (1f - threat);    // far from where you should be
            hold *= Style(p.Style, entry: 0.75f, anchor: 1.4f);

            // Advance: go to the objective. Time pressure drives attackers, a planted spike drives defenders.
            float adv = 0.15f + 0.35f * a;
            if (s.Attacking)
            {
                adv += 0.6f * s.TimePressure;
                if (s.CarriesSpike) adv += 0.15f;
                if (s.SpikePlanted && !s.AtObjective) adv += 0.4f;
            }
            else adv += s.SpikePlanted ? 0.7f : 0.1f * a;
            if (!s.AtObjective) adv += 0.35f * Profile.Clamp01(s.ObjectiveDistance / 25f);
            else adv *= 0.3f;
            adv -= 0.5f * c * Profile.Clamp01((s.AheadOfTeam - p.MaxLead) / 8f);             // wait for the team
            adv -= 0.3f * threat * (1f - a);
            adv *= Style(p.Style, entry: 1.3f, lurker: 1.1f, support: 0.9f);

            // Peek: challenge an enemy you believe is there but cannot see.
            float peek = 0f;
            if (s.EnemiesVisible == 0 && s.ThreatCertainty > 0.25f && s.NearestThreat < 30f)
            {
                peek = (0.2f + 0.5f * a + 0.2f * odds + 0.15f * p.Skill + 0.1f * s.Health) * Style(p.Style, entry: 1.35f, anchor: 0.6f);
                if (holdingSide && s.AtObjective) peek *= 0.4f + 0.6f * a;   // leaving a good position needs a reason
            }

            // FallBack: give ground when hurt or outnumbered and something is close.
            float back = 0f;
            if (threat > 0.1f)
                back = (0.6f * (1f - s.Health) + 0.35f * Math.Max(0f, -odds) + 0.25f * (1f - a)) * threat * 1.5f * Style(p.Style, lurker: 1.2f);

            // Trade: go where a teammate just died.
            float trade = s.TradeAvailable ? (0.35f + 0.45f * c + 0.15f * a) * Style(p.Style, support: 1.3f, lurker: 0.5f) : 0f;

            scores[0] = Math.Max(0f, hold);
            scores[1] = Math.Max(0f, adv);
            scores[2] = Math.Max(0f, peek);
            scores[3] = Math.Max(0f, back);
            scores[4] = Math.Max(0f, trade);
        }

        /// <summary>Best scoring choice. The current action gets a small bonus so agents do not flip every decision, and
        /// less skilled agents get more noise (worse reads).</summary>
        public static AgentAction Pick(float[] scores, AgentAction current, Profile p, Random rng)
        {
            int best = 0;
            float bestScore = float.MinValue, noise = 0.12f * (1f - p.Skill);
            for (int i = 0; i < Choices.Length; i++)
            {
                if (scores[i] <= 0f) continue;
                float v = scores[i] + (Choices[i] == current ? 0.08f : 0f) + (float)rng.NextDouble() * noise;
                if (v > bestScore) { bestScore = v; best = i; }
            }
            return Choices[best];
        }

        static float Style(PlayStyle s, float entry = 1f, float support = 1f, float lurker = 1f, float anchor = 1f) =>
            s == PlayStyle.Entry ? entry : s == PlayStyle.Support ? support : s == PlayStyle.Lurker ? lurker : s == PlayStyle.Anchor ? anchor : 1f;
    }
}
