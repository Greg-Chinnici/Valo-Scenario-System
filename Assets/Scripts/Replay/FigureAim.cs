using UnityEngine;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Runs after the Animator each frame on an <see cref="AnimatedFigure"/>: bends the spine by the view pitch on top
    /// of the animation, and puts the rifle in the right hand pointing where the player looks.
    /// </summary>
    public sealed class FigureAim : MonoBehaviour
    {
        public Transform[] spine = new Transform[0];
        public Transform hand, gun, aimSpace;
        public Vector3 gunOffset;
        public float spineShare = 0.7f;
        public float pitch;

        private void LateUpdate()
        {
            if (aimSpace == null) return;
            float p = Mathf.Clamp(pitch, -70f, 70f);
            if (spine.Length > 0)
            {
                var bend = Quaternion.AngleAxis(p * spineShare / spine.Length, aimSpace.right);
                foreach (var bone in spine) if (bone != null) bone.rotation = bend * bone.rotation;
            }
            if (gun != null && hand != null)
            {
                var aim = aimSpace.rotation * Quaternion.Euler(p, 0f, 0f);
                gun.SetPositionAndRotation(hand.position + aim * gunOffset, aim);
            }
        }
    }
}
