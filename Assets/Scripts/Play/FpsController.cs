using UnityEngine;
using UnityEngine.InputSystem;

namespace ValoReplay.Play
{
    /// <summary>
    /// First-person movement close to Valorant's: run 6.75 m/s, walk (Shift) 3.8, crouch (Ctrl) 3.4, jump (Space).
    /// The transform is the capsule centre, like replay positions (1.0 m above the floor). Mouse look with recoil
    /// that recovers. Sensitivity, invert Y and field of view come from <see cref="GameSettings"/> (live).
    /// Uses the scene's Main Camera if there is one, otherwise makes its own.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class FpsController : MonoBehaviour
    {
        public float runSpeed = 6.75f, walkSpeed = 3.8f, crouchSpeed = 3.4f;
        public float groundAcceleration = 60f, airAcceleration = 12f, friction = 10f;
        public float gravity = 20f, jumpSpeed = 6.3f;
        public float standingHeight = 2f, crouchHeight = 1.4f, radius = 0.35f;
        public float eyeAboveCentre = 0.62f;
        public float recoilRecovery = 9f;

        public Camera Camera { get; private set; }
        public Vector3 Velocity => _velocity;
        public bool Grounded => _controller.isGrounded;
        public bool Crouching { get; private set; }
        public bool Walking { get; private set; }
        public bool InputEnabled { get; set; } = true;
        /// <summary>Horizontal speed as a 0..1 fraction of run speed (for spread).</summary>
        public float MoveFraction => new Vector3(_velocity.x, 0f, _velocity.z).magnitude / runSpeed;

        private CharacterController _controller;
        private Transform _pivot;
        private float _yaw, _pitch, _recoil, _eyeOffset;
        private Vector3 _velocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _controller.height = standingHeight;
            _controller.radius = radius;
            _controller.center = Vector3.zero;
            _controller.stepOffset = 0.45f;
            _controller.slopeLimit = 50f;
            _controller.skinWidth = 0.03f;

            _pivot = new GameObject("View").transform;
            _pivot.SetParent(transform, false);
            _eyeOffset = eyeAboveCentre;
            _pivot.localPosition = Vector3.up * _eyeOffset;

            // adopt the scene camera (keeps whatever is set up on it), or make one
            Camera = Camera.main;
            if (Camera == null)
            {
                Camera = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
                Camera.gameObject.AddComponent<AudioListener>();
            }
            Camera.transform.SetParent(_pivot, false);
            Camera.transform.localPosition = Vector3.zero;
            Camera.transform.localRotation = Quaternion.identity;
            Camera.nearClipPlane = 0.04f;
            Camera.farClipPlane = Mathf.Max(Camera.farClipPlane, 3000f);
        }

        /// <summary>Place the player at a replay pose: capsule centre, Unity yaw, Unity pitch (down positive).</summary>
        public void Teleport(Vector3 centre, float yaw, float pitch, bool crouching)
        {
            _controller.enabled = false;
            transform.position = centre;
            _controller.enabled = true;
            _yaw = yaw;
            _pitch = Mathf.Clamp(pitch, -89f, 89f);
            _velocity = Vector3.zero;
            SetCrouch(crouching, force: true);
            ApplyLook();
        }

        /// <summary>Kick the view up (degrees) with a little horizontal jitter.</summary>
        public void AddRecoil(float up, float sideways)
        {
            _recoil += up;
            _yaw += sideways;
        }

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            Vector2 move = Vector2.zero;
            bool jump = false, walk = false, crouch = false;
            if (InputEnabled && kb != null)
            {
                if (kb.wKey.isPressed) move.y += 1;
                if (kb.sKey.isPressed) move.y -= 1;
                if (kb.dKey.isPressed) move.x += 1;
                if (kb.aKey.isPressed) move.x -= 1;
                jump = kb.spaceKey.wasPressedThisFrame;
                walk = kb.leftShiftKey.isPressed;
                crouch = kb.leftCtrlKey.isPressed || kb.cKey.isPressed;
            }
            if (InputEnabled && mouse != null)
            {
                var settings = GameSettings.Current;
                var d = mouse.delta.ReadValue();
                _yaw += d.x * settings.sensitivity;
                _pitch = Mathf.Clamp(_pitch - d.y * settings.sensitivity * (settings.invertY ? -1f : 1f), -89f, 89f);
            }

            SetCrouch(crouch, force: false);
            Walking = walk && !Crouching;
            float speed = Crouching ? crouchSpeed : Walking ? walkSpeed : runSpeed;

            var wish = Quaternion.Euler(0f, _yaw, 0f) * new Vector3(move.x, 0f, move.y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            var horizontal = new Vector3(_velocity.x, 0f, _velocity.z);
            float dt = Time.deltaTime;
            if (_controller.isGrounded)
            {
                // friction, then accelerate toward the wished velocity
                horizontal = Vector3.MoveTowards(horizontal, wish * speed, (wish.sqrMagnitude > 0f ? groundAcceleration : friction * speed) * dt);
                _velocity.y = jump ? jumpSpeed : -2f;
            }
            else
            {
                horizontal = Vector3.MoveTowards(horizontal, wish * speed, airAcceleration * dt);
                _velocity.y -= gravity * dt;
            }
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;

            var flags = _controller.Move(_velocity * dt);
            if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0f) _velocity.y = 0f;

            _recoil = Mathf.MoveTowards(_recoil, 0f, recoilRecovery * dt * Mathf.Max(1f, _recoil));
            _pivot.localPosition = Vector3.Lerp(_pivot.localPosition, Vector3.up * _eyeOffset, 1f - Mathf.Exp(-15f * dt));
            ApplyLook();
        }

        private void ApplyLook()
        {
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _pivot.localRotation = Quaternion.Euler(Mathf.Clamp(_pitch - _recoil, -89f, 89f), 0f, 0f);
            Camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(GameSettings.Current.horizontalFov, Mathf.Max(0.1f, Camera.aspect));
        }

        private void SetCrouch(bool crouch, bool force)
        {
            if (crouch == Crouching && !force) return;
            if (!crouch && Crouching && !force)
            {
                // only stand up if there is headroom
                float extra = standingHeight - crouchHeight;
                var top = transform.position + Vector3.up * (crouchHeight * 0.5f - 0.3f - radius);
                if (Physics.SphereCast(top, radius * 0.9f, Vector3.up, out _, extra, ~0, QueryTriggerInteraction.Ignore)) return;
            }
            Crouching = crouch;
            // keep the feet where they are: the capsule shrinks from the top
            _controller.height = crouch ? crouchHeight : standingHeight;
            _controller.center = new Vector3(0f, crouch ? -(standingHeight - crouchHeight) * 0.5f : 0f, 0f);
            _eyeOffset = crouch ? eyeAboveCentre - 0.55f : eyeAboveCentre;
            if (force) _pivot.localPosition = Vector3.up * _eyeOffset;
        }
    }
}
