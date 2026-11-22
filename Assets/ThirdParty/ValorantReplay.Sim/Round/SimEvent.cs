// SimEvent.cs - what happened in a round, for hosts and analysis: kills (KillRecord) and the event log (SimEvent).
// Besides what happened (kills, the spike), the log says what the AI meant to do: each agent's decisions and
// objective, the team's callouts, and the plan's calls (execute, rotate, retake), so a round can be read back as why.
using System.Numerics;

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
    /// or a death.
    /// <para>What the AI meant to do. Decided: Agent's brain chose Action (and Swing for a peek or a trade) about enemy
    /// Other (SimAgent.Focus, -1 = none); raised when one of the three changes, not for every re-decision. Objective:
    /// the director gave Agent a new objective, Position, further than its radius from the last one. CalloutMade /
    /// CalloutDropped: Agent called out (or saw and did not say) where enemy Other is, Position = what it saw or heard.
    /// CalloutHeard: that callout reached Team's board and changed what the board believes, Position = as told (off by
    /// the caller's error). Executed / Rotated: Team's attack goes onto Site (Rotated: to Site; on defence Site is where
    /// the rotation went). Retake: Team's defenders go in on the planted spike.</para></summary>
    public enum SimEventType : byte
    {
        Kill = 1, Diverged = 2, Planted = 3, Defused = 4, SpikeDropped = 5, SpikePickedUp = 6, Detonated = 7, Hit = 8, PlantStarted = 9, DefuseStarted = 10, OrderDone = 11,
        Decided = 12, Objective = 13, CalloutMade = 14, CalloutDropped = 15, CalloutHeard = 16, Executed = 17, Rotated = 18, Retake = 19,
    }

    public struct SimEvent
    {
        public float Time;
        public SimEventType Type;
        public int Agent, Other;            // Kill: victim, killer; Diverged/Spike*: agent; Other -1 if unused
        public int Team;                    // Agent's team; the team for Executed, Rotated, Retake; -1 = none
        public AgentAction Action;          // Decided
        public PeekStyle Swing;             // Decided
        public Vector3 Position;            // Objective, Callout*
        public string Site;                 // Executed, Rotated

        public override string ToString() => $"{Time,6:0.00} {Type} {Agent}{(Other < 0 ? "" : Type >= SimEventType.Decided ? " -> " + Other : " <- " + Other)}" +
            (Type == SimEventType.Decided ? $" {Action}{(Swing != PeekStyle.None ? " " + Swing : "")}" : "") +
            (Site != null ? " " + Site : "");
    }
}
