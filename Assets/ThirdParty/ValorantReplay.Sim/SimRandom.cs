// SimRandom.cs - independent random streams for one simulated round. Round-level draws (spike carrier, target site) have
// their own stream, and every agent has one stream per purpose (aim, decisions, senses, callouts, where to look). A change
// to one agent (its profile, a decision) then leaves everyone else's dice alone: two variants run with the same seed differ
// only where the change itself makes the round play out differently (common random numbers), which makes A/B comparisons
// far less noisy than with one shared stream.
using System;

namespace ValorantReplay.Sim
{
    public enum RandomStream { Round = 0, Aim = 1, Decide = 2, Senses = 3, Comms = 4, Look = 5 }

    public static class SimRandom
    {
        /// <summary>Seed of one stream: a hash of the run's seed, the agent (-1 for the round itself) and the purpose.</summary>
        public static int StreamSeed(int seed, int agent, RandomStream stream)
        {
            ulong x = (uint)seed;
            x = Mix(x ^ ((ulong)(uint)(agent + 1) << 32));
            x = Mix(x ^ (ulong)stream);
            return (int)(x & 0x7fffffff);
        }

        /// <summary>Seed of the index-th run of a batch derived from a base seed (and optionally a scenario key), so
        /// neighbouring runs and scenarios do not share streams.</summary>
        public static int RunSeed(int seed, int index, int scenario = 0)
        {
            ulong x = Mix((uint)seed ^ ((ulong)(uint)scenario << 32));
            return (int)(Mix(x ^ (uint)index) & 0x7fffffff);
        }

        public static Random Create(int seed, int agent, RandomStream stream) => new Random(StreamSeed(seed, agent, stream));

        // SplitMix64 finaliser
        static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
