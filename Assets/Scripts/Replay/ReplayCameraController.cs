using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Orbit camera for the replay: right-drag orbits, left/middle-drag pans, wheel zooms, WASD pans, Q/E rotate.
    /// Left-click a player to follow them (the pivot tracks them); dragging or Esc stops following.
    /// Ignores the pointer while it is over the HUD. <see cref="Clicked"/> can take clicks over (command mode).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class ReplayCameraController : MonoBehaviour
    {
        [SerializeField] private ReplayViewer viewer;
        [SerializeField] private UIDocument hud;
        [SerializeField] private float orbitSpeed = 0.25f;
        [SerializeField] private float panSpeed = 1f;
        [SerializeField] private float keyPanSpeed = 1.2f;
        [SerializeField] private float followSharpness = 10f;
        [SerializeField] private float minDistance = 3f, maxDistance = 250f;

        private Camera _camera;
        private Vector3 _pivot;
        private float _distance = 80f, _yaw, _pitch = 70f;
        private int _dragButton = -1;     // 0 left, 1 right, 2 middle
        private Vector2 _dragTravel;

        /// <summary>A click without a drag on the world: screen position and button (0 left, 1 right). True = handled,
        /// so a left click does not also follow a player.</summary>
        public System.Func<Vector2, int, bool> Clicked { get; set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (viewer == null) viewer = FindFirstObjectByType<ReplayViewer>();
        }

        private void OnEnable() { if (viewer != null) viewer.Loaded += FrameReplay; }
        private void OnDisable() { if (viewer != null) viewer.Loaded -= FrameReplay; }

        /// <summary>Top-down view of the whole play area.</summary>
        public void FrameReplay()
        {
            if (viewer == null || viewer.Replay == null) return;
            var b = viewer.PlayArea;
            _pivot = new Vector3(b.center.x, b.min.y, b.center.z);
            _yaw = 0f;
            _pitch = 70f;
            float size = Mathf.Max(b.size.x, b.size.z);
            _distance = Mathf.Clamp(size * 0.5f / Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f, minDistance, maxDistance);
            maxDistance = Mathf.Max(maxDistance, _distance * 2f);
            ApplyTransform();
        }

        /// <summary>The orbit: pivot, distance, yaw and pitch (for bookmarks).</summary>
        public (Vector3 Pivot, float Distance, float Yaw, float Pitch) View
        {
            get => (_pivot, _distance, _yaw, _pitch);
            set
            {
                _pivot = value.Pivot;
                _distance = Mathf.Clamp(value.Distance, minDistance, maxDistance);
                _yaw = value.Yaw;
                _pitch = Mathf.Clamp(value.Pitch, 5f, 89f);
                ApplyTransform();
            }
        }

        private void LateUpdate()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse != null) HandleMouse(mouse);
            if (keyboard != null) HandleKeyboard(keyboard);

            if (viewer != null && viewer.Replay != null && viewer.FollowPlayer >= 0 && viewer.FollowPlayer < viewer.Replay.Players.Count)
            {
                var target = viewer.PlayerPosition(viewer.FollowPlayer);
                _pivot = Vector3.Lerp(_pivot, target, 1f - Mathf.Exp(-followSharpness * Time.unscaledDeltaTime));
            }
            ApplyTransform();
        }

        private void HandleMouse(Mouse mouse)
        {
            Vector2 pos = mouse.position.ReadValue();
            Vector2 delta = mouse.delta.ReadValue();
            bool overUi = IsOverHud(pos);

            if (_dragButton < 0 && !overUi)
            {
                if (mouse.leftButton.wasPressedThisFrame) StartDrag(0);
                else if (mouse.rightButton.wasPressedThisFrame) StartDrag(1);
                else if (mouse.middleButton.wasPressedThisFrame) StartDrag(2);
            }

            if (_dragButton >= 0)
            {
                _dragTravel += new Vector2(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
                var button = _dragButton == 0 ? mouse.leftButton : _dragButton == 1 ? mouse.rightButton : mouse.middleButton;
                if (_dragButton == 1)
                {
                    _yaw += delta.x * orbitSpeed;
                    _pitch = Mathf.Clamp(_pitch - delta.y * orbitSpeed, 5f, 89f);
                }
                else if (_dragTravel.sqrMagnitude > 16f)
                {
                    Pan(-delta * (_distance * 0.0015f * panSpeed));
                }
                if (button.wasReleasedThisFrame)
                {
                    bool click = _dragTravel.sqrMagnitude <= 16f;
                    if (click && (Clicked == null || !Clicked(pos, _dragButton)) && _dragButton == 0) PickPlayer(pos);
                    _dragButton = -1;
                }
            }

            if (!overUi)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    _distance = Mathf.Clamp(_distance * (scroll > 0 ? 0.88f : 1f / 0.88f), minDistance, maxDistance);
            }
        }

        private void StartDrag(int button)
        {
            _dragButton = button;
            _dragTravel = Vector2.zero;
        }

        private void HandleKeyboard(Keyboard kb)
        {
            var move = Vector2.zero;
            if (kb.wKey.isPressed) move.y += 1;
            if (kb.sKey.isPressed) move.y -= 1;
            if (kb.dKey.isPressed) move.x += 1;
            if (kb.aKey.isPressed) move.x -= 1;
            if (move != Vector2.zero) Pan(move * (_distance * keyPanSpeed * Time.unscaledDeltaTime));
            if (kb.qKey.isPressed) _yaw -= 90f * Time.unscaledDeltaTime;
            if (kb.eKey.isPressed) _yaw += 90f * Time.unscaledDeltaTime;
            if (kb.escapeKey.wasPressedThisFrame && viewer != null) viewer.FollowPlayer = -1;
        }

        /// <summary>Moves the pivot on the ground plane; x = screen right, y = screen up (away from the camera).</summary>
        private void Pan(Vector2 amount)
        {
            if (viewer != null) viewer.FollowPlayer = -1;
            var yawRot = Quaternion.Euler(0f, _yaw, 0f);
            _pivot += yawRot * new Vector3(amount.x, 0f, amount.y);
        }

        private void PickPlayer(Vector2 screenPos)
        {
            if (viewer == null || viewer.Replay == null) return;
            // nearest player along the ray: the map may be loaded but hidden (minimap view), so look past it
            var ray = _camera.ScreenPointToRay(screenPos);
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(ray, 1000f))
            {
                int p = viewer.PlayerOf(hit.collider);
                if (p >= 0 && hit.distance < bestDistance) { best = p; bestDistance = hit.distance; }
            }
            if (best >= 0) viewer.FollowPlayer = best;
        }

        private void ApplyTransform()
        {
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.SetPositionAndRotation(_pivot + rot * new Vector3(0f, 0f, -_distance), rot);
        }

        /// <summary>True when the pointer is over a pickable HUD element (panels, buttons, timeline).</summary>
        private bool IsOverHud(Vector2 screenPos)
        {
            var panel = hud != null ? hud.rootVisualElement?.panel : null;
            if (panel == null) return false;
            var panelPos = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenPos.x, Screen.height - screenPos.y));
            var picked = panel.Pick(panelPos);
            return picked != null && picked != panel.visualTree && picked != hud.rootVisualElement;
        }
    }
}
