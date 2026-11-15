// RoundSim.Spike.cs - the spike for AI agents: picking it up, planting, defusing, detonation.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        void Objectives(float dt)
        {
            switch (SpikeState)
            {
                case SpikeStatus.Carried:
                {
                    var c = agents[SpikeCarrier];
                    SpikePosition = c.Position;
                    if (c.Mode == AgentMode.Replay) break;   // the recording plants (FollowRecording)
                    if (c.Action == AgentAction.Plant)
                    {
                        // keep planting through a fight only when there is no time left to win it first
                        bool mustCommit = Remaining < Options.PlantDuration - c.TaskProgress + 1.5f;
                        if (c.Mode == AgentMode.Ai && c.Order.Type != OrderType.Plant && AnyVisible(c) && !mustCommit) { c.Action = AgentAction.None; c.TaskProgress = 0f; c.WantsDecision = true; break; }
                        c.Moving = false;
                        c.TaskProgress += dt;
                        if (c.TaskProgress >= Options.PlantDuration) Plant(c, c.Position);
                    }
                    else if (c.Mode == AgentMode.Ai && c.Order.Type == OrderType.None && (!AnyVisible(c) || Remaining < Options.PlantDuration + 1.5f) && Map.InSite(Map.NearestSite(c.Position), c.Position))
                    {
                        c.Action = AgentAction.Plant;
                        c.TaskProgress = 0f;
                        c.Path.Clear();
                        Event(SimEventType.PlantStarted, c.Index);
                    }
                    break;
                }
                case SpikeStatus.Dropped:
                    foreach (var a in agents)
                    {
                        if (!a.Alive || !a.Attacking || a.Mode == AgentMode.Replay || Vector3.Distance(a.Position, SpikePosition) > 1.2f) continue;
                        SpikeState = SpikeStatus.Carried;
                        SpikeCarrier = a.Index;
                        spikeFromRecording = false;
                        Event(SimEventType.SpikePickedUp, a.Index);
                        if (a.Order.Type == OrderType.PickUp) EndOrder(a, true);
                        foreach (var m in agents) if (m.Alive && m.Attacking) m.WantsDecision = true;
                        break;
                    }
                    break;
                case SpikeStatus.Planted:
                    if (SpikeTimer <= 0f)
                    {
                        SpikeState = SpikeStatus.Detonated;
                        Event(SimEventType.Detonated, -1);
                        break;
                    }
                    foreach (var a in agents)
                    {
                        if (!a.Alive || a.Attacking || a.Mode == AgentMode.Replay) continue;
                        if (a.Action == AgentAction.Defuse)
                        {
                            // keep defusing through a fight only when there is no time left to win it first
                            bool mustCommit = SpikeTimer < Options.DefuseDuration - a.TaskProgress + 1.5f;
                            if (a.Mode == AgentMode.Ai && a.Order.Type != OrderType.Defuse && AnyVisible(a) && !mustCommit) { a.Action = AgentAction.None; a.TaskProgress = 0f; a.WantsDecision = true; continue; }
                            a.Moving = false;
                            a.TaskProgress += dt;
                            if (a.TaskProgress >= Options.DefuseDuration) { Defuse(a); return; }
                        }
                        else if (a.Mode == AgentMode.Ai && a.Order.Type == OrderType.None && Vector3.Distance(a.Position, SpikePosition) <= 1.5f && !AnyVisible(a))
                        {
                            a.Action = AgentAction.Defuse;
                            a.TaskProgress = 0f;
                            a.Path.Clear();
                            Event(SimEventType.DefuseStarted, a.Index);
                        }
                    }
                    break;
            }
        }

        void Plant(SimAgent c, Vector3 at)
        {
            SpikeState = SpikeStatus.Planted;
            SpikePosition = at;
            SpikeTimer = Options.SpikeDuration;
            SpikeCarrier = -1;
            c.Action = AgentAction.None;
            c.TaskProgress = 0f;
            Event(SimEventType.Planted, c.Index);
            if (c.Order.Type == OrderType.Plant) EndOrder(c, true);
            foreach (var m in agents) if (m.Alive) m.WantsDecision = true;
        }

        void Defuse(SimAgent d)
        {
            SpikeState = SpikeStatus.Defused;
            d.Action = AgentAction.None;
            Event(SimEventType.Defused, d.Index);
            if (d.Order.Type == OrderType.Defuse) EndOrder(d, true);
        }
    }
}
