using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValoReplay.Replay
{
    /// <summary>
    /// A player drawn with the installed humanoid character (<see cref="CharacterRegistry"/>). Its Animator gets the
    /// player's velocity in its own frame (MoveX, MoveZ in m/s), Crouch and Airborne; the controller built by the set-up
    /// menu blends idle, walk, run and strafe clips by that velocity. The Animator runs at replay speed (paused replay =
    /// frozen pose). Root motion is off: the replay or the AI moves the player. <see cref="FigureAim"/> adds the view
    /// pitch and the rifle after the Animator.
    /// </summary>
    public sealed class AnimatedFigure : IPlayerFigure
    {
        private static readonly int MoveX = Animator.StringToHash("MoveX"), MoveZ = Animator.StringToHash("MoveZ");
        private static readonly int CrouchId = Animator.StringToHash("Crouch"), AirborneId = Animator.StringToHash("Airborne");

        public Transform Root { get; }

        private readonly Transform _model;
        private readonly Animator _animator;
        private readonly FigureAim _aim;
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly List<Renderer> _gunRenderers = new List<Renderer>();
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        private bool _visible = true, _hasLast;
        private Vector3 _last, _velocity;
        private float _ground;
        private readonly Vector2[] _standPoints, _crouchPoints;
        private readonly float _maxPace;

        public AnimatedFigure(Transform parent, CharacterRegistry reg)
        {
            _standPoints = reg.standPoints ?? new Vector2[0];
            _crouchPoints = reg.crouchPoints ?? new Vector2[0];
            _maxPace = Mathf.Max(1f, reg.maxPace);
            Root = new GameObject("Character").transform;
            Root.SetParent(parent, false);
            var go = UnityEngine.Object.Instantiate(reg.model, Root, false);
            go.name = reg.model.name;
            _model = go.transform;
            _model.localPosition = new Vector3(0f, -1f, 0f);          // feet on the floor below the capsule centre
            _model.localRotation = Quaternion.identity;

            _animator = go.GetComponent<Animator>();
            if (_animator == null || _animator.avatar == null || !_animator.avatar.isHuman)
            {
                UnityEngine.Object.Destroy(Root.gameObject);
                throw new InvalidOperationException($"{reg.model.name} is not imported as a Humanoid (run Set Up Mixamo Character again)");
            }
            _animator.runtimeAnimatorController = reg.controller;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = PlayerFigures.DefaultLit;   // imported materials are not URP
                r.sharedMaterials = mats;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                _renderers.Add(r);
            }

            _aim = go.AddComponent<FigureAim>();
            _aim.aimSpace = Root;
            _aim.hand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
            _aim.gunOffset = reg.gunOffset;
            _aim.spineShare = reg.spinePitchShare;
            var spine = new List<Transform>();
            foreach (var b in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
            {
                var t = _animator.GetBoneTransform(b);
                if (t != null) spine.Add(t);
            }
            _aim.spine = spine.ToArray();
            if (reg.showGun && _aim.hand != null) _aim.gun = BuildRifle(Root);
        }

        private Transform BuildRifle(Transform parent)
        {
            var gun = new GameObject("Rifle").transform;
            gun.SetParent(parent, false);
            void Box(Vector3 pos, Vector3 size)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(b.GetComponent<Collider>());
                b.transform.SetParent(gun, false);
                b.transform.localPosition = pos;
                b.transform.localScale = size;
                var r = b.GetComponent<Renderer>();
                _gunRenderers.Add(r);
                _renderers.Add(r);
            }
            Box(new Vector3(0f, 0.03f, 0.14f), new Vector3(0.055f, 0.08f, 0.62f));     // body and barrel
            Box(new Vector3(0f, -0.04f, 0f), new Vector3(0.04f, 0.1f, 0.05f));         // grip
            Box(new Vector3(0f, -0.04f, 0.2f), new Vector3(0.035f, 0.1f, 0.06f));      // magazine
            return gun;
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            foreach (var r in _renderers) r.enabled = visible;
        }

        /// <summary>First material slot in the colour, the others (joints, details) darker; the rifle dark.</summary>
        public void Tint(Color color)
        {
            var dark = Color.Lerp(color, new Color(0.08f, 0.09f, 0.11f), 0.7f);
            dark.a = color.a;
            foreach (var r in _renderers)
            {
                bool gun = _gunRenderers.Contains(r);
                int slots = Mathf.Max(1, r.sharedMaterials.Length);
                for (int i = 0; i < slots; i++)
                {
                    var c = gun || i > 0 ? dark : color;
                    _block.SetColor("_BaseColor", c);
                    _block.SetColor("_Color", c);
                    r.SetPropertyBlock(_block, i);
                }
            }
        }

        public void UseMaterial(Material material, bool castShadows)
        {
            if (material == null) return;
            foreach (var r in _renderers)
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = material;
                r.sharedMaterials = mats;
                r.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        public void ResetMotion() => _hasLast = false;

        /// <summary>Whether the clips have this velocity covered: standing still needs an idle, moving needs a clip within
        /// 45 degrees of the direction of travel.</summary>
        public static bool Covers(Vector2 velocity, Vector2[] points)
        {
            if (points == null || points.Length == 0) return false;
            float speed = velocity.magnitude;
            foreach (var p in points)
            {
                float m = p.magnitude;
                if (speed < 0.3f) { if (m < 0.25f) return true; continue; }
                if (m >= 0.25f && Vector2.Dot(p / m, velocity / speed) >= 0.7f) return true;
            }
            return false;
        }

        /// <summary>How much faster than recorded the clips must play at this velocity: 1 up to the fastest clip within
        /// 45 degrees of the direction of travel (its speed along that direction), then speed / that, capped.</summary>
        public static float Pace(Vector2 velocity, Vector2[] points, float maxPace)
        {
            float speed = velocity.magnitude;
            if (speed < 0.3f || points == null || points.Length == 0) return 1f;
            var dir = velocity / speed;
            float reach = 0f;
            foreach (var p in points)
            {
                float m = p.magnitude;
                if (m < 0.25f) continue;
                float cos = Vector2.Dot(p / m, dir);
                if (cos >= 0.7f) reach = Mathf.Max(reach, m * cos);
            }
            return reach > 0f ? Mathf.Clamp(speed / reach, 1f, maxPace) : 1f;
        }

        public void Animate(Vector3 worldPosition, float yaw, float dt, float pitch, bool crouching, bool airborne, float floorOffset = 0f)
        {
            if (!_hasLast) { _last = worldPosition; _hasLast = true; }
            if (dt <= 0f) { _animator.speed = 0f; return; }
            var delta = worldPosition - _last;
            _last = worldPosition;
            delta.y = 0f;
            if (delta.magnitude > 3f) delta = Vector3.zero;            // a seek or a snap
            var local = Quaternion.Euler(0f, -yaw, 0f) * (delta / dt);
            _velocity = Vector3.Lerp(_velocity, local, 1f - Mathf.Exp(-10f * dt));

            // game speeds are far beyond mocap: past the fastest clip in this direction, blend at that clip's speed and
            // play it faster, so the stride still matches the ground covered
            var flat = new Vector2(_velocity.x, _velocity.z);
            // crouched only where there is a crouched clip for this direction; otherwise the standing one beats a slide
            bool crouchClip = crouching && Covers(flat, _crouchPoints);
            float pace = Pace(flat, crouchClip ? _crouchPoints : _standPoints, _maxPace);
            _animator.SetFloat(MoveX, flat.x / pace);
            _animator.SetFloat(MoveZ, flat.y / pace);
            _animator.SetBool(CrouchId, crouchClip);
            _animator.SetBool(AirborneId, airborne);
            // the Animator runs on Unity's clock: scale it to replay time (slow motion, x4, paused)
            float frame = Time.deltaTime;
            _animator.speed = frame > 1e-5f ? Mathf.Clamp(dt / frame * (airborne ? 1f : pace), 0f, 8f) : 0f;

            _ground = Mathf.Lerp(_ground, Mathf.Clamp(floorOffset, -0.4f, 0.4f), 1f - Mathf.Exp(-10f * dt));
            _model.localPosition = new Vector3(0f, -1f + _ground, 0f);
            _aim.pitch = pitch;
        }
    }
}
