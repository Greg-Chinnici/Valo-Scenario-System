// RoundSim.Recording.cs - replay-mode agents follow the .vrp (positions, kills, the spike) until they perceive
// something that did not happen in it; then they diverge and the AI takes over.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        /// <summary>The round of replay at time (seconds into the replay), as a simulation. Agent i is player i. With
        /// followRecording every agent replays what it really did until it diverges; otherwise all start as AI. Profiles
        /// default to ones fitted from the same replay (ProfileFitter), so a diverged player keeps playing like itself.
        /// A time before the barrier drop starts at the barrier drop. attack: the attackers' plan (null = drawn from
        /// options.AttackPlans, else DefaultAttack).</summary>
        public static RoundSim FromReplay(VrpReplay replay, SimMap map, float time, Func<int, Profile> profiles = null,
            SimOptions options = null, int seed = 0, bool followRecording = true, AttackPlan attack = null)
        {
            options = options ?? new SimOptions();
            int ri = replay.RoundAt(time);
            if (ri < 0) throw new ArgumentException("replay has no rounds");
            var round = replay.Rounds[ri];
            if (round.AttackingTeam == 255) throw new ArgumentException($"round {ri + 1}: attacking team unknown");
            if (round.BarrierTime >= 0f && time < round.BarrierTime) time = round.BarrierTime;
            if (time >= round.EndTime) throw new ArgumentException($"round {ri + 1} is already over at {time:0.0}s");
            if (profiles == null)
            {
                var fitted = ProfileFitter.Fit(replay, map: map);
                profiles = p => fitted[p].Profile;
            }

            var setup = new List<AgentSetup>();
            for (int p = 0; p < replay.Players.Count; p++)
            {
                var f = replay.Sample(p, time);
                var lo = replay.LoadoutOf(ri, p);
                string held = replay.WeaponOf(f);
                string gun = !string.IsNullOrEmpty(lo?.Primary) ? lo.Primary : !string.IsNullOrEmpty(lo?.Sidearm) ? lo.Sidearm : held;
                int team = replay.Players[p].Team;
                if (team > 1) throw new ArgumentException($"player {p}: team {team} (only two-team matches are supported)");
                setup.Add(new AgentSetup
                {
                    Team = team, Attacking = team == round.AttackingTeam, Position = SimSpace.FromReplay(replay, f), Yaw = f.Yaw,
                    Alive = f.Alive, Health = f.Health, Armor = f.Armor + f.Shield, Profile = profiles(p), Weapon = gun, Loadout = WeaponStats.LoadoutValue(lo),
                    Name = replay.Players[p].DisplayName, Mode = followRecording ? AgentMode.Replay : AgentMode.Ai,
                });
            }

            var rs = replay.RoundStateAt(time);
            var rsetup = new RoundSetup
            {
                Attack = attack,
                // the recorded clock is sampled only when it changes: trust a combat-phase value (counted down from when it
                // was sampled), not the buy timer's last value that is still there at the barrier drop
                Remaining = Math.Max(1f, rs.Phase == RoundPhase.Combat && rs.Remaining > 1f ? rs.Remaining - (time - rs.Time)
                    : options.RoundTime - (time - Math.Max(round.StartTime, round.BarrierTime))),
            };
            if (round.PlantTime >= 0f && round.PlantTime <= time)
            {
                rsetup.SpikePlanted = true;
                rsetup.SpikePosition = SimSpace.FromReplay(replay, round.PlantX, round.PlantY, round.PlantZ);
                rsetup.SpikeTimer = Math.Max(0.5f, options.SpikeDuration - (time - round.PlantTime));
            }
            else
            {
                int holder = SpikeHolderAt(replay, time, out float since);
                if (holder < replay.Players.Count) rsetup.SpikeCarrier = holder;
                else if (holder == VrpReplay.HolderNone && since >= 0f)
                {
                    // on the ground: where the last holder was when it left them
                    int last = LastSpikeHolderBefore(replay, since);
                    if (last >= 0)
                    {
                        rsetup.SpikeDropped = true;
                        rsetup.SpikePosition = SimSpace.FromReplay(replay, replay.Sample(last, since));
                    }
                }
            }

            var sim = new RoundSim(map, setup, rsetup, options, seed) { SourceStart = time };
            if (followRecording)
            {
                sim.Source = replay;
                sim.sourceRound = round;
                sim.spikeFromRecording = sim.SpikeState != SpikeStatus.Planted && replay.SpikeCarriers.Count > 0;
                while (sim.killCursor < replay.Kills.Count && replay.Kills[sim.killCursor].Time <= time) sim.killCursor++;
            }
            return sim;
        }

        void FollowRecording(float dt)
        {
            float rt0 = SourceStart + Time - dt, rt = SourceStart + Time;
            if (rt >= sourceRound.EndTime)
            {
                // the recording has no more of this round
                foreach (var a in agents) if (a.Alive && a.Mode == AgentMode.Replay) Diverge(a);
                Source = null;
                return;
            }
            foreach (var a in agents)
            {
                if (!a.Alive || a.Mode != AgentMode.Replay) continue;
                var f = Source.Sample(a.Index, rt);
                var pos = SimSpace.FromReplay(Source, f);
                a.Moving = SimSpace.FlatDistance(pos, a.Position) / dt > 1f;
                a.Position = pos;
                a.Yaw = f.Yaw;
                a.Walking = f.Walking;
                a.Crouching = f.Crouching;
                if (f.Alive) { a.Health = f.Health; a.Armor = f.Armor + f.Shield; }
            }

            var kills = Source.Kills;
            for (; killCursor < kills.Count && kills[killCursor].Time <= rt; killCursor++)
            {
                var k = kills[killCursor];
                if (k.Time <= rt0 || k.Victim >= agents.Count) continue;
                var v = agents[k.Victim];
                var killer = k.Killer < agents.Count ? agents[k.Killer] : null;
                if (v.Alive && v.Mode == AgentMode.Replay)
                {
                    if (killer == null || killer == v) Kill(v, null, false);
                    else if (killer.Mode == AgentMode.Replay && killer.Alive) Kill(v, killer, false);
                    else Diverge(v);      // its killer is no longer where the recording had it
                }
                else if (killer != null && killer.Alive && killer.Mode == AgentMode.Replay) Diverge(killer);   // its victim is elsewhere
            }
            // deaths with no kill entry (fall damage, spike). Frames are read at the tick at or before rt, and a death
            // with a kill entry just ahead waits for it, so a kill is never mistaken for a death without a killer.
            foreach (var a in agents)
                if (a.Alive && a.Mode == AgentMode.Replay && !Source.GetFrame(Source.TickAt(rt), a.Index).Alive && !KillAhead(a.Index, rt))
                    Kill(a, null, false);

            if (spikeFromRecording && (SpikeState == SpikeStatus.Carried || SpikeState == SpikeStatus.Dropped))
            {
                int holder = SpikeHolderAt(Source, rt, out _);
                if (holder < agents.Count)
                {
                    var h = agents[holder];
                    if (h.Alive && h.Mode == AgentMode.Replay)
                    {
                        if (SpikeState != SpikeStatus.Carried || SpikeCarrier != holder) Event(SimEventType.SpikePickedUp, holder);
                        SpikeState = SpikeStatus.Carried;
                        SpikeCarrier = holder;
                    }
                    else spikeFromRecording = false;
                }
                else if (holder == VrpReplay.HolderNone && SpikeState == SpikeStatus.Carried && agents[SpikeCarrier].Mode == AgentMode.Replay)
                {
                    Event(SimEventType.SpikeDropped, SpikeCarrier);
                    SpikeState = SpikeStatus.Dropped;
                    SpikePosition = agents[SpikeCarrier].Position;
                    SpikeCarrier = -1;
                }
            }
            if (sourceRound.PlantTime > rt0 && sourceRound.PlantTime <= rt && SpikeState == SpikeStatus.Carried && sourceRound.Planter < agents.Count)
            {
                var p = agents[sourceRound.Planter];
                if (p.Alive && p.Mode == AgentMode.Replay && SpikeCarrier == p.Index)
                    Plant(p, SimSpace.FromReplay(Source, sourceRound.PlantX, sourceRound.PlantY, sourceRound.PlantZ));
            }
            if (sourceRound.DefuseTime > rt0 && sourceRound.DefuseTime <= rt && SpikeState == SpikeStatus.Planted && sourceRound.Defuser < agents.Count)
            {
                var d = agents[sourceRound.Defuser];
                if (d.Alive && d.Mode == AgentMode.Replay) Defuse(d);
            }
        }

        bool KillAhead(int victim, float rt)
        {
            var kills = Source.Kills;
            for (int i = killCursor; i < kills.Count && kills[i].Time <= rt + 0.5f; i++)
                if (kills[i].Victim == victim) return true;
            return false;
        }

        void Diverge(SimAgent a)
        {
            if (a.Mode != AgentMode.Replay) return;
            a.Mode = AgentMode.Ai;
            a.DivergedAt = Time;
            a.WantsDecision = true;
            a.Path.Clear();
            a.PathGoal = new Vector3(float.NaN);
            if (SpikeCarrier == a.Index) spikeFromRecording = false;
            Event(SimEventType.Diverged, a.Index);
        }

        static int SpikeHolderAt(VrpReplay r, float time, out float since)
        {
            int h = VrpReplay.HolderNone;
            since = -1f;
            foreach (var s in r.SpikeCarriers)
            {
                if (s.Time > time) break;
                if (s.Holder != h) since = s.Time;
                h = s.Holder;
            }
            return h;
        }

        static int LastSpikeHolderBefore(VrpReplay r, float time)
        {
            int last = -1;
            foreach (var s in r.SpikeCarriers)
            {
                if (s.Time > time) break;
                if (s.Holder < r.Players.Count) last = s.Holder;
            }
            return last;
        }
    }
}
