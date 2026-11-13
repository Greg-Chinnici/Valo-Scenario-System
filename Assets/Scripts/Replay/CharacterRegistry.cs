using UnityEngine;

namespace ValoReplay.Replay
{
    /// <summary>
    /// The animated player character: a humanoid model and the animator built for it. Written by
    /// ValoReplay > Characters > Set Up Mixamo Character to Assets/Characters/Resources/PlayerCharacter.asset;
    /// without it players use the built-in mannequin.
    /// </summary>
    public sealed class CharacterRegistry : ScriptableObject
    {
        public const string ResourcePath = "PlayerCharacter";

        [Tooltip("Humanoid model (the character FBX).")]
        public GameObject model;
        [Tooltip("Animator with parameters MoveX, MoveZ (local velocity, m/s), Crouch and Airborne.")]
        public RuntimeAnimatorController controller;
        [Tooltip("Draw a rifle in the right hand, pointing where the player looks.")]
        public bool showGun = true;
        [Tooltip("Rifle position from the right hand, in the player's aim frame (x right, y up, z forward), metres.")]
        public Vector3 gunOffset = new Vector3(-0.02f, 0.02f, 0.14f);
        [Tooltip("How much of the view pitch the spine bends (the rest is in the gun).")]
        [Range(0f, 1f)] public float spinePitchShare = 0.7f;

        [Header("Written by the set-up menu")]
        [Tooltip("Velocity (x right, y forward, m/s) of each standing clip in the blend tree.")]
        public Vector2[] standPoints = new Vector2[0];
        [Tooltip("Velocity of each crouched clip; empty = no crouch animations.")]
        public Vector2[] crouchPoints = new Vector2[0];
        [Tooltip("Most the animation may be sped up to keep the feet planted at game speeds (Valorant runs far faster than mocap).")]
        [Range(1f, 4f)] public float maxPace = 2.6f;

        private static CharacterRegistry _instance;
        private static bool _looked;

        public static CharacterRegistry Instance
        {
            get
            {
                if (!_looked)
                {
                    _instance = Resources.Load<CharacterRegistry>(ResourcePath);
                    _looked = true;
                }
                return _instance;
            }
        }

        /// <summary>Look again (after the editor set-up wrote a new one).</summary>
        public static void Reload() => _looked = false;
    }
}
