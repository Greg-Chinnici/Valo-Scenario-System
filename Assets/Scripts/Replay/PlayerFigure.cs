using System;
using UnityEngine;

namespace ValoReplay.Replay
{
    /// <summary>
    /// What a player looks like: the installed animated character (<see cref="AnimatedFigure"/>, set up with
    /// ValoReplay > Characters > Set Up Mixamo Character) or, without one, the built-in <see cref="Mannequin"/>.
    /// The root sits at the player's capsule centre, like replay positions (floor 1.0 m below). Figures have no
    /// colliders: hits are tested against the player's own capsule.
    /// </summary>
    public interface IPlayerFigure
    {
        Transform Root { get; }
        void SetVisible(bool visible);
        /// <summary>Main colour (team, gold, grey when eliminated); alpha is used with a transparent material.</summary>
        void Tint(Color color);
        /// <summary>Replaces every material (e.g. a transparent one for a ghost).</summary>
        void UseMaterial(Material material, bool castShadows);
        /// <summary>After a seek or a teleport: the next move is not a step.</summary>
        void ResetMotion();
        /// <summary>
        /// Moves the animation on by <paramref name="dt"/> replay seconds (0 freezes it, so a paused replay holds the pose)
        /// from where the root is now. <paramref name="pitch"/>: view pitch in degrees, down positive.
        /// <paramref name="floorOffset"/>: how far the real floor is from 1 m below the root.
        /// </summary>
        void Animate(Vector3 worldPosition, float yaw, float dt, float pitch, bool crouching, bool airborne, float floorOffset = 0f);
    }

    public static class PlayerFigures
    {
        /// <summary>The installed character if there is one, else the mannequin.</summary>
        public static IPlayerFigure Create(Transform parent)
        {
            var reg = CharacterRegistry.Instance;
            if (reg != null && reg.model != null && reg.controller != null)
            {
                try { return new AnimatedFigure(parent, reg); }
                catch (Exception e)
                {
                    if (!_warned) Debug.LogWarning($"[Characters] Using the mannequin: {e.Message}");
                    _warned = true;
                }
            }
            return new Mannequin(parent);
        }

        private static bool _warned;
        private static Material _lit;

        /// <summary>The render pipeline's default lit material (the one primitives get), shared by every figure.</summary>
        public static Material DefaultLit
        {
            get
            {
                if (_lit != null) return _lit;
                var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _lit = probe.GetComponent<Renderer>().sharedMaterial;
                UnityEngine.Object.Destroy(probe);
                return _lit;
            }
        }
    }
}
