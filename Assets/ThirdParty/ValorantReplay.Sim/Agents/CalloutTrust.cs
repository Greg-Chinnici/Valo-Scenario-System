// CalloutTrust.cs - how much each agent believes each teammate's callouts: its Profile.Trust, unless a host set a value
// for that pair (RoundSim.SetTrust).
namespace ValorantReplay.Sim
{
    sealed class CalloutTrust
    {
        readonly int agents;
        float[,] overrides;     // NaN = no override; null until the first one

        public CalloutTrust(int agents) => this.agents = agents;

        public void Set(int reader, int caller, float trust)
        {
            if (overrides == null)
            {
                overrides = new float[agents, agents];
                for (int i = 0; i < agents; i++) for (int j = 0; j < agents; j++) overrides[i, j] = float.NaN;
            }
            overrides[reader, caller] = float.IsNaN(trust) ? float.NaN : Profile.Clamp01(trust);
        }

        /// <summary>Multiplier on the certainty of what caller told reader (caller -1: an outside source).</summary>
        public float Scale(SimAgent reader, int caller)
        {
            float t = overrides != null && caller >= 0 && !float.IsNaN(overrides[reader.Index, caller]) ? overrides[reader.Index, caller] : reader.Profile.Trust;
            return Profile.Lerp(0.5f, 1.5f, t);
        }
    }
}
