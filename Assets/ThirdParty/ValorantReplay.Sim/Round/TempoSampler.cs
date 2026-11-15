// TempoSampler.cs - feeds a simulated round's RoundTempo: every RoundTempo.Interval, where each agent is, where it was
// and whether it is alive, the same samples Calibration takes from recorded rounds.
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    sealed class TempoSampler
    {
        readonly RoundTempo tempo;
        readonly List<TempoPlayer> players = new List<TempoPlayer>();
        Vector3[] previous;
        bool[] wasAlive;
        float clock;

        public TempoSampler(RoundTempo tempo) => this.tempo = tempo;

        /// <summary>Takes everyone's starting state the first time it is called (the round's first Step).</summary>
        public void Start(IReadOnlyList<SimAgent> agents)
        {
            if (previous != null) return;
            previous = new Vector3[agents.Count];
            wasAlive = new bool[agents.Count];
            for (int i = 0; i < agents.Count; i++) { previous[i] = agents[i].Position; wasAlive[i] = agents[i].Alive; }
        }

        public void Advance(float dt, float time, SimMap map, bool planted, IReadOnlyList<SimAgent> agents)
        {
            clock += dt;
            if (clock < RoundTempo.Interval - 1e-4f) return;
            clock = 0f;
            players.Clear();
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                players.Add(new TempoPlayer { Attacking = a.Attacking, Alive = a.Alive, WasAlive = wasAlive[i], Position = a.Position, Previous = previous[i] });
                previous[i] = a.Position;
                wasAlive[i] = a.Alive;
            }
            tempo.Sample(time, map, planted, players);
        }
    }
}
