using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using ValorantReplay.Playback;
using ValoReplay.Replay;

namespace ValoReplay.Play
{
    /// <summary>
    /// First-person spectating in round play: V moves the camera behind another agent's eyes (their recorded view, or
    /// the AI's once it plays them); LMB / → next, RMB / ← previous; V again returns to your agent. Targets are everyone
    /// still up, including the gold ghost of your own agent's original run. It also works after you die.
    /// </summary>
    public sealed class SpectatorController : MonoBehaviour
    {
        public float rotationSharpness = 25f;
        public float autoAdvanceDelay = 1.2f;

        public bool Active { get; private set; }
        public int Target { get; private set; } = -1;

        private RoundPlayController _round;
        private ReplayViewer _ghosts;
        private Camera _camera;
        private Transform _homeParent;
        private float _goneSince = -1f;

        public void Init(RoundPlayController round, ReplayViewer ghosts)
        {
            _round = round;
            _ghosts = ghosts;
        }

        public void Toggle()
        {
            if (Active) Exit();
            else Enter();
        }

        public void Enter()
        {
            if (Active || _round == null || _round.Fps == null) return;
            var targets = Targets();
            if (targets.Count == 0) return;
            _camera = _round.Fps.Camera;
            _homeParent = _camera.transform.parent;
            _camera.transform.SetParent(null, true);
            // start on your agent's original run if it is still going, else the first ally
            int start = targets.Contains(_round.Player) ? _round.Player : targets[0];
            Active = true;
            SetTarget(start, snap: true);
            _round.SetSpectating(true);
        }

        public void Exit()
        {
            if (!Active) return;
            Active = false;
            _ghosts.FirstPersonTarget = -1;
            Target = -1;
            if (_camera != null)
            {
                _camera.transform.SetParent(_homeParent, false);
                _camera.transform.localPosition = Vector3.zero;
                _camera.transform.localRotation = Quaternion.identity;
            }
            _round.SetSpectating(false);
        }

        /// <summary>Alive, on-map, not eliminated; allies first, then enemies.</summary>
        private List<int> Targets()
        {
            var list = new List<int>();
            var r = _round.Replay;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < r.Players.Count; i++)
                {
                    bool ally = r.Players[i].Team == _round.Team;
                    if ((pass == 0) != ally) continue;
                    if (_round.IsUp(i)) list.Add(i);
                }
            return list;
        }

        private void Cycle(int dir)
        {
            var targets = Targets();
            if (targets.Count == 0) { Exit(); return; }
            int i = targets.IndexOf(Target);
            int next = i < 0 ? 0 : ((i + dir) % targets.Count + targets.Count) % targets.Count;
            SetTarget(targets[next], snap: true);
        }

        private void SetTarget(int player, bool snap)
        {
            Target = player;
            _ghosts.FirstPersonTarget = player;
            _goneSince = -1f;
            if (snap) Place(1f);
        }

        private void Update()
        {
            if (_round == null || _round.Replay == null || _round.Fps == null || _round.Paused) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.vKey.wasPressedThisFrame) { Toggle(); return; }
            if (!Active) return;

            if ((mouse != null && mouse.leftButton.wasPressedThisFrame) || (kb != null && kb.rightArrowKey.wasPressedThisFrame)) Cycle(1);
            else if ((mouse != null && mouse.rightButton.wasPressedThisFrame) || (kb != null && kb.leftArrowKey.wasPressedThisFrame)) Cycle(-1);
        }

        private void LateUpdate()
        {
            if (!Active || _round == null || _round.Replay == null) return;
            bool gone = !_round.IsUp(Target);
            if (gone)
            {
                // hold on the death for a moment, then move on (like the game)
                if (_goneSince < 0f) _goneSince = Time.unscaledTime;
                else if (Time.unscaledTime - _goneSince > autoAdvanceDelay) Cycle(1);
                return;
            }
            _goneSince = -1f;
            Place(1f - Mathf.Exp(-rotationSharpness * Time.unscaledDeltaTime));
        }

        /// <summary>Camera at the target's eyes, easing toward their view angles (recorded, or the AI's).</summary>
        private void Place(float blend)
        {
            if (_camera == null) return;
            _round.ViewOf(Target, out var eye, out var view);
            _camera.transform.position = eye;
            _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, view, blend);
        }
    }
}
