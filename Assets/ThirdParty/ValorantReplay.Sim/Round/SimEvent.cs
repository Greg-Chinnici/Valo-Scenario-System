// SimEvent.cs - what happened in a round, for hosts and analysis: kills (KillRecord) and the event log (SimEvent).

namespace ValorantReplay.Sim
{
    public struct KillRecord
    {
        public float Time;
        public int Killer, Victim;          // agent / player indices; Killer -1 = no player (spike, fall)
        public int KillerTeam, VictimTeam;
        public bool Simulated;              // false = taken from the recording
    }

    /// <summary>Hit: Agent was damaged by Other (for a host's own senses: damage indicators, hit sounds). PlantStarted /
    /// DefuseStarted: Agent began planting / defusing (the spike makes noise from then on). OrderDone: Agent's order is
    /// over, Other 1 = carried out, 0 = could not be (the spike moved on, no way there); not raised for a cleared order
    /// or a death.</summary>
    public enum SimEventType : byte { Kill = 1, Diverged = 2, Planted = 3, Defused = 4, SpikeDropped = 5, SpikePickedUp = 6, Detonated = 7, Hit = 8, PlantStarted = 9, DefuseStarted = 10, OrderDone = 11 }

    public struct SimEvent
    {
        public float Time;
        public SimEventType Type;
        public int Agent, Other;            // Kill: victim, killer; Diverged/Spike*: agent; Other -1 if unused
        public override string ToString() => $"{Time,6:0.00} {Type} {Agent}{(Other >= 0 ? " <- " + Other : "")}";
    }
}
