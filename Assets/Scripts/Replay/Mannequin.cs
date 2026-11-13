using System.Collections.Generic;
using UnityEngine;

namespace ValoReplay.Replay
{
    /// <summary>
    /// A partially rigged human figure for players: a bone hierarchy (hips, spine, head, two-segment arms and legs)
    /// with rigid primitive parts, about 1.8 m tall, holding a rifle. It animates itself procedurally from how its root
    /// moves: a walk / run cycle whose stride follows the distance travelled (feet do not slide, and it follows replay
    /// speed and pause), strafing and backpedalling legs, a crouch, a tuck in the air, and arms placed on the gun by
    /// two-bone IK while the gun follows the view pitch.
    /// The root sits at the player's capsule centre, like replay positions (floor 1.0 m below). No colliders: hits are
    /// tested against the player's own capsule.
    /// </summary>
    public sealed class Mannequin : IPlayerFigure
    {
        // proportions (m), from the floor at y = -1 below the capsule centre
        private const float Floor = -1f;
        private const float HipHeight = 0.95f;      // hip joint above the floor
        private const float Thigh = 0.42f, Shin = 0.42f;
        private const float UpperArm = 0.29f, Forearm = 0.28f;
        private static readonly Vector3 GunPivot = new Vector3(0.06f, 0.36f, 0.22f);   // on the spine: front right of the chest
        private const float RunSpeed = 6.75f;       // Valorant run speed with a rifle (m/s)

        public Transform Root { get; }
        public bool Visible { get; private set; } = true;

        private readonly Transform _hips, _spine, _head, _gun;
        private readonly Transform[] _thigh = new Transform[2], _shin = new Transform[2], _upperArm = new Transform[2], _forearm = new Transform[2];
        private readonly List<Renderer> _body = new List<Renderer>(), _skin = new List<Renderer>(), _dark = new List<Renderer>(), _visor = new List<Renderer>();
        private readonly List<Renderer> _all = new List<Renderer>();
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        private Vector3 _lastPos;
        private bool _hasLast;
        private float _phase, _speed, _crouch, _air, _ground;
        private Vector3 _moveLocal = Vector3.forward;

        // ------------------------------------------------------------------ building

        public Mannequin(Transform parent, string name = "Mannequin")
        {
            Root = new GameObject(name).transform;
            Root.SetParent(parent, false);

            _hips = Bone("Hips", Root, new Vector3(0f, Floor + HipHeight, 0f));
            Part(PrimitiveType.Cube, _hips, new Vector3(0f, 0.02f, 0f), new Vector3(0.3f, 0.17f, 0.19f), _dark);

            _spine = Bone("Spine", _hips, new Vector3(0f, 0.06f, 0f));
            Part(PrimitiveType.Capsule, _spine, new Vector3(0f, 0.27f, 0f), new Vector3(0.36f, 0.25f, 0.22f), _body);       // torso
            Part(PrimitiveType.Cube, _spine, new Vector3(0f, 0.3f, -0.12f), new Vector3(0.28f, 0.3f, 0.06f), _dark);        // backpack plate

            _head = Bone("Head", _spine, new Vector3(0f, 0.55f, 0f));
            Part(PrimitiveType.Sphere, _head, new Vector3(0f, 0.12f, 0f), new Vector3(0.22f, 0.24f, 0.23f), _skin);
            Part(PrimitiveType.Cube, _head, new Vector3(0f, 0.14f, 0.1f), new Vector3(0.17f, 0.05f, 0.05f), _visor);        // eyes: shows facing

            for (int s = 0; s < 2; s++)
            {
                float x = s == 0 ? -1f : 1f;      // 0 = left, 1 = right
                _thigh[s] = Bone(s == 0 ? "Thigh.L" : "Thigh.R", _hips, new Vector3(0.1f * x, -0.05f, 0f));
                Part(PrimitiveType.Capsule, _thigh[s], new Vector3(0f, -Thigh / 2f, 0f), new Vector3(0.15f, Thigh / 2f + 0.02f, 0.15f), _dark);
                _shin[s] = Bone(s == 0 ? "Shin.L" : "Shin.R", _thigh[s], new Vector3(0f, -Thigh, 0f));
                Part(PrimitiveType.Capsule, _shin[s], new Vector3(0f, -Shin / 2f, 0f), new Vector3(0.12f, Shin / 2f + 0.02f, 0.12f), _dark);
                Part(PrimitiveType.Cube, _shin[s], new Vector3(0f, -Shin - 0.04f, 0.05f), new Vector3(0.11f, 0.08f, 0.25f), _dark);   // boot

                _upperArm[s] = Bone(s == 0 ? "UpperArm.L" : "UpperArm.R", _spine, new Vector3(0.22f * x, 0.48f, 0f));
                Part(PrimitiveType.Capsule, _upperArm[s], new Vector3(0f, -UpperArm / 2f, 0f), new Vector3(0.11f, UpperArm / 2f + 0.03f, 0.11f), _body);
                _forearm[s] = Bone(s == 0 ? "Forearm.L" : "Forearm.R", _upperArm[s], new Vector3(0f, -UpperArm, 0f));
                Part(PrimitiveType.Capsule, _forearm[s], new Vector3(0f, -Forearm / 2f, 0f), new Vector3(0.09f, Forearm / 2f + 0.02f, 0.09f), _skin);
            }

            // the rifle: pivots at the right side of the chest and follows the view pitch
            _gun = Bone("Gun", _spine, GunPivot);
            Part(PrimitiveType.Cube, _gun, new Vector3(0f, 0f, 0.18f), new Vector3(0.06f, 0.09f, 0.62f), _dark);           // body and barrel
            Part(PrimitiveType.Cube, _gun, new Vector3(0f, -0.08f, 0.04f), new Vector3(0.045f, 0.12f, 0.05f), _dark);      // grip
            Part(PrimitiveType.Cube, _gun, new Vector3(0f, -0.08f, 0.24f), new Vector3(0.04f, 0.11f, 0.06f), _dark);       // magazine

            Pose(0f, 0f);
        }

        private static Transform Bone(string name, Transform parent, Vector3 localPosition)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            return t;
        }

        private void Part(PrimitiveType type, Transform bone, Vector3 position, Vector3 scale, List<Renderer> group)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = type.ToString();
            var col = go.GetComponent<Collider>();
            col.enabled = false;              // bullets and sight lines must not stop on the figure
            Object.Destroy(col);
            go.transform.SetParent(bone, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            group.Add(r);
            _all.Add(r);
        }

        // ------------------------------------------------------------------ looks

        public void SetVisible(bool visible)
        {
            if (visible == Visible) return;
            Visible = visible;
            foreach (var r in _all) r.enabled = visible;
        }

        /// <summary>Colours the figure: clothes in the team colour, a lighter head, dark legs and gun, bright eyes.
        /// Alpha carries over (for a transparent material).</summary>
        public void Tint(Color team)
        {
            float a = team.a;
            Set(_body, team);
            Set(_skin, WithAlpha(Color.Lerp(team, Color.white, 0.55f), a));
            Set(_dark, WithAlpha(Color.Lerp(team, new Color(0.08f, 0.09f, 0.11f), 0.75f), a));
            Set(_visor, WithAlpha(Color.Lerp(team, Color.white, 0.85f), a));
        }

        private void Set(List<Renderer> group, Color c)
        {
            _block.SetColor("_BaseColor", c);
            _block.SetColor("_Color", c);
            foreach (var r in group) r.SetPropertyBlock(_block);
        }

        private static Color WithAlpha(Color c, float a) { c.a = a; return c; }

        /// <summary>Replaces every part's material (e.g. a transparent one for a ghost); shadows off for it.</summary>
        public void UseMaterial(Material material, bool castShadows)
        {
            foreach (var r in _all)
            {
                r.sharedMaterial = material;
                r.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        // ------------------------------------------------------------------ animation

        /// <summary>Forget the last position (after a seek or a teleport) so the next step does not read as a sprint.</summary>
        public void ResetMotion() => _hasLast = false;

        /// <summary>
        /// Advances the animation. <paramref name="dt"/> is the time the position moved over (replay seconds, so a paused
        /// replay freezes the pose and a fast one runs the cycle faster); 0 or less keeps the current pose.
        /// <paramref name="pitch"/> is the view pitch in degrees, down positive. <paramref name="floorOffset"/> moves the
        /// feet onto the real floor when the position is a little above or below it.
        /// </summary>
        public void Animate(Vector3 worldPosition, float yaw, float dt, float pitch, bool crouching, bool airborne, float floorOffset = 0f)
        {
            if (!_hasLast) { _lastPos = worldPosition; _hasLast = true; }
            if (dt <= 0f) return;
            var delta = worldPosition - _lastPos;
            _lastPos = worldPosition;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist > 3f) dist = 0f;                                   // a seek or a snap: no step
            float speed = dist / dt;

            float k = 1f - Mathf.Exp(-12f * dt);                          // smoothing per replay second
            _speed = Mathf.Lerp(_speed, speed, k);
            _crouch = Mathf.Lerp(_crouch, crouching ? 1f : 0f, 1f - Mathf.Exp(-14f * dt));
            _air = Mathf.Lerp(_air, airborne ? 1f : 0f, 1f - Mathf.Exp(-14f * dt));
            _ground = Mathf.Lerp(_ground, Mathf.Clamp(floorOffset, -0.4f, 0.4f), 1f - Mathf.Exp(-10f * dt));
            if (dist > 1e-4f)
            {
                var local = Quaternion.Euler(0f, -yaw, 0f) * (delta / dist);
                _moveLocal = Vector3.Slerp(_moveLocal, local, k);
            }

            // one full cycle (two steps) covers more ground the faster you go
            float speed01 = Mathf.Clamp01(_speed / RunSpeed);
            float stride = Mathf.Lerp(1.1f, 2.5f, speed01) * (crouching ? 0.6f : 1f);
            _phase = Mathf.Repeat(_phase + 2f * Mathf.PI * dist / stride, 2f * Mathf.PI);
            Pose(speed01, pitch);
            Root.localPosition = new Vector3(0f, _ground, 0f);
        }

        /// <summary>Sets every bone from the current phase, speed, crouch and air blends.</summary>
        private void Pose(float speed01, float pitch)
        {
            float moving = Mathf.Clamp01(_speed / 0.6f);                 // 0 standing, 1 walking or faster
            float swing = Mathf.Lerp(22f, 38f, speed01) * moving * (1f - 0.5f * _crouch) * (1f - _air);
            var move = new Vector3(_moveLocal.x, 0f, _moveLocal.z);
            if (move.sqrMagnitude < 1e-4f) move = Vector3.forward;
            move.Normalize();
            var swingAxis = Vector3.Cross(Vector3.up, move);            // legs swing in the direction of travel

            // hips: drop for the crouch, bob with each step
            float bob = 0.035f * moving * Mathf.Abs(Mathf.Sin(_phase));
            _hips.localPosition = new Vector3(0f, Floor + HipHeight - 0.3f * _crouch - bob + 0.08f * _air, 0f);
            _hips.localRotation = Quaternion.Euler(0f, 6f * moving * Mathf.Sin(_phase) * move.z, 0f);

            for (int s = 0; s < 2; s++)
            {
                float ph = _phase + (s == 0 ? 0f : Mathf.PI);
                float legSwing = swing * Mathf.Sin(ph);
                // the knee lifts while the leg swings forward, and stays a little bent while running
                float knee = moving * (Mathf.Lerp(48f, 75f, speed01) * Mathf.Max(0f, Mathf.Cos(ph)) + 8f * speed01) * (1f - _crouch);
                var walk = Quaternion.AngleAxis(-legSwing, swingAxis);
                var crouch = Quaternion.Euler(-55f, 0f, 0f);
                var tuck = Quaternion.Euler(-35f, 0f, 0f);
                _thigh[s].localRotation = Quaternion.Slerp(Quaternion.Slerp(walk, crouch, _crouch), tuck, _air);
                float kneeAngle = Mathf.Lerp(Mathf.Lerp(knee, 100f, _crouch), 70f, _air);
                _shin[s].localRotation = Quaternion.Euler(kneeAngle, 0f, 0f);
            }

            // torso: lean into the run and forward in a crouch; a little counter-twist against the hips
            float lean = 7f * speed01 + 14f * _crouch;
            _spine.localRotation = Quaternion.AngleAxis(lean, swingAxis.sqrMagnitude > 0f && moving > 0.1f ? swingAxis : Vector3.right)
                                   * Quaternion.Euler(0f, -6f * moving * Mathf.Sin(_phase) * move.z, 0f);
            // undo most of the travel lean for the head and gun so the aim stays level, then look up or down
            float p = Mathf.Clamp(pitch, -70f, 70f);
            _head.localRotation = Quaternion.Euler(0.6f * p - 0.5f * lean, 0f, 0f);
            float gunBob = 0.012f * moving * Mathf.Sin(2f * _phase);
            _gun.localPosition = GunPivot + new Vector3(0f, gunBob, 0f);
            _gun.localRotation = Quaternion.Euler(p - lean, 0f, 0f);

            // arms reach the gun: right hand on the grip, left hand under the front
            ReachArm(1, _gun.TransformPoint(new Vector3(0f, -0.1f, 0.04f)), side: 1f);
            ReachArm(0, _gun.TransformPoint(new Vector3(0f, -0.06f, 0.2f)), side: -1f);
        }

        /// <summary>Two-bone IK: shoulder to hand, elbow bending down and out toward a pole beside and below the shoulder.</summary>
        private void ReachArm(int s, Vector3 hand, float side)
        {
            var shoulder = _upperArm[s].position;
            var pole = _spine.TransformPoint(new Vector3(0.45f * side, 0.1f, -0.15f));
            var to = hand - shoulder;
            float d = Mathf.Clamp(to.magnitude, 0.05f, UpperArm + Forearm - 0.005f);
            var dir = to.sqrMagnitude > 1e-8f ? to.normalized : _spine.forward;
            // law of cosines: angle at the shoulder between the reach line and the upper arm
            float cos = (UpperArm * UpperArm + d * d - Forearm * Forearm) / (2f * UpperArm * d);
            float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
            var toPole = pole - shoulder;
            var bend = toPole - dir * Vector3.Dot(toPole, dir);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : -_spine.up;
            var elbow = shoulder + dir * (UpperArm * cos) + bend * (UpperArm * sin);
            _upperArm[s].rotation = Quaternion.FromToRotation(Vector3.down, (elbow - shoulder).normalized);
            _forearm[s].rotation = Quaternion.FromToRotation(Vector3.down, (shoulder + dir * d - elbow).normalized);
        }
    }
}
