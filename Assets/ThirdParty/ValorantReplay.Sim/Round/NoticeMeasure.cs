// NoticeMeasure.cs - counts simulated agents' notices, crosshair placement and reactions with Measurements' definitions,
// so simulated shooting compares number for number with recorded shooting.
using System;

namespace ValorantReplay.Sim
{
    sealed class NoticeMeasure
    {
        // per (agent, enemy): in view (without the "shot at by" shortcut), last time in view, and the pending notice a
        // reaction is timed from (-1 = none)
        readonly bool[,] inView;
        readonly float[,] lastInView, noticedAt;

        public NoticeMeasure(int agents)
        {
            inView = new bool[agents, agents];
            lastInView = new float[agents, agents];
            noticedAt = new float[agents, agents];
            for (int i = 0; i < agents; i++)
                for (int j = 0; j < agents; j++) { lastInView[i, j] = float.NegativeInfinity; noticedAt[i, j] = -1f; }
        }

        /// <summary>Whether a has b in view this perception tick. Coming into view after NoticeGap out of it is a notice.</summary>
        public void Look(SimAgent a, SimAgent b, bool view, float time)
        {
            int i = a.Index, j = b.Index;
            if (view && !inView[i, j] && time - lastInView[i, j] >= Measurements.NoticeGap)
            {
                a.Shooting.Notices++;
                a.Shooting.AimErrorSum += Math.Abs(SimMath.DeltaYaw(a.Yaw, SimSpace.YawOf(b.Position - a.Position)));
                noticedAt[i, j] = time;
            }
            if (view) lastInView[i, j] = time;
            inView[i, j] = view;
        }

        /// <summary>a fired at target: the first shot after a notice is a reaction if it is at that enemy and in time; any
        /// shot ends the wait.</summary>
        public void Shot(SimAgent a, int target, float time)
        {
            for (int j = 0; j < noticedAt.GetLength(1); j++)
            {
                float at = noticedAt[a.Index, j];
                if (at < 0f) continue;
                if (j == target && time - at <= Measurements.ReactionWindow) { a.Shooting.Reactions++; a.Shooting.ReactionSum += time - at; }
                noticedAt[a.Index, j] = -1f;
            }
        }
    }
}
