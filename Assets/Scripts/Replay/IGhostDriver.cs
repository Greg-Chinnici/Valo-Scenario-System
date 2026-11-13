using System.Collections.Generic;
using UnityEngine;

namespace ValoReplay.Replay
{
    /// <summary>Where a driven player is now, as the driver sees it.</summary>
    public struct GhostPose
    {
        public bool Visible;        // false = dead or off the map
        public Vector3 Position;    // capsule centre, like replay positions
        public float Yaw;
        public bool Crouching;
    }

    /// <summary>A death marker the driver wants drawn.</summary>
    public struct GhostDeath
    {
        public int Victim;
        public Vector3 Position;    // capsule centre where they died
    }

    /// <summary>
    /// Takes over parts of a <see cref="ReplayViewer"/> in round play: the round AI decides where players are, what they
    /// did and who died, so those players and their actions no longer come from the recording. Set
    /// <see cref="ReplayViewer.Driver"/>; null = everything plays as recorded.
    /// </summary>
    public interface IGhostDriver
    {
        /// <summary>False = this player plays as recorded; true = <paramref name="pose"/> says where it is.</summary>
        bool TryGetPose(int player, out GhostPose pose);

        /// <summary>True = what the recording has this player do at this replay time (shots, utility) did not happen.</summary>
        bool RecordingHides(int player, float time);

        /// <summary>False = the spike plays as recorded; true = <paramref name="visible"/> and <paramref name="position"/>
        /// say where it is (on the floor or held).</summary>
        bool TryGetSpike(out Vector3 position, out bool visible);

        /// <summary>Every death of the current round, replacing the recorded ones.</summary>
        IReadOnlyList<GhostDeath> Deaths { get; }

        /// <summary>Health and armor (ability shields included) of a player it drives, and their gun; false = as recorded.</summary>
        bool TryGetStatus(int player, out int health, out int armor, out string gun);
    }
}
