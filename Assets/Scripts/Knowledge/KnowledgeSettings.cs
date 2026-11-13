using System;
using UnityEngine;

namespace ValoReplay.Knowledge
{
    /// <summary>
    /// Tuning for <see cref="KnowledgeSim"/>: how often the senses run, how far agents see, and how far each sound
    /// carries. Sound ranges are estimates, not values from the game; tune them to taste.
    /// </summary>
    [Serializable]
    public sealed class KnowledgeSettings
    {
        [Tooltip("Sensor updates per second (sight and hearing are checked at this rate).")]
        public float sensorRate = 10f;

        [Header("Sight (naive: view cone + one ray to the head and one to the body, blocked by map geometry and smokes)")]
        [Tooltip("Horizontal field of view in degrees (Valorant: 103).")]
        public float fieldOfView = 103f;
        public float sightRange = 120f;
        [Tooltip("Sight range while nearsighted (Reyna leer, inside a smoke, ...).")]
        public float nearsightRange = 5f;

        [Header("Sound falloff: audible up to this range (m); certainty = 1 - (distance / range)^2")]
        public float footstepRange = 40f;
        public float landingRange = 30f;
        public float gunshotRange = 100f;
        [Tooltip("Suppressed guns: Ghost, Spectre.")]
        public float suppressedGunshotRange = 35f;
        public float reloadRange = 12f;
        public float abilityRange = 30f;
        public float spikeRange = 50f;
        [Tooltip("Horizontal speed (m/s) above which steps that are neither walked nor crouched are audible.")]
        public float footstepSpeed = 3f;
        [Tooltip("Seconds between two footstep sounds of one player.")]
        public float footstepInterval = 0.4f;
        [Tooltip("Position blur (m) of a sound heard right next to you / at the edge of its range.")]
        public float soundBlurNear = 1.5f, soundBlurFar = 10f;

        [Header("Belief decay")]
        [Tooltip("Seconds for a position's certainty to halve once nobody sees or hears the enemy.")]
        public float certaintyHalfLife = 6f;
        [Tooltip("How fast an unseen enemy could have moved (m/s): the uncertainty radius grows at this rate.")]
        public float enemyMoveSpeed = 5.4f;
        public float maxRadius = 40f;
        [Tooltip("Seconds a sighted utility object stays on the board after it was last seen.")]
        public float utilityMemory = 10f;

        [Header("Economy")]
        [Tooltip("Valorant's scoreboard shows every player's ult points, so enemy ult progress is public.")]
        public bool ultPointsPublic = true;

        [Header("Area beliefs (probability of each enemy per callout area)")]
        public bool areaBeliefs = true;
        [Tooltip("Size (m) of the standable cells learned from recorded positions.")]
        public float areaCellSize = 1.5f;
        [Tooltip("Points per area checked for visibility (more = finer 'nobody is there' evidence, more raycasts).")]
        public int areaProbes = 5;
        [Tooltip("Share of the time an unseen enemy is on the move between areas (0..1).")]
        public float areaMobility = 0.25f;
        [Tooltip("How fast belief drains from an area allies are looking at without seeing anyone (per second, fully in view).")]
        public float areaClearRate = 2f;
    }
}
