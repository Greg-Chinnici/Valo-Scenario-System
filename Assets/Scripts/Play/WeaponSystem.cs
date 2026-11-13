using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Random = UnityEngine.Random;

namespace ValoReplay.Play
{
    /// <summary>
    /// The guns the player carries (primary / sidearm / knife): hitscan fire with spread, recoil, magazines and reloads.
    /// 1 / 2 / 3 switch, R reloads, LMB fires. What a shot hit is reported through <see cref="Hit"/>.
    /// </summary>
    public sealed class WeaponSystem : MonoBehaviour
    {
        public sealed class Gun
        {
            public WeaponStats Stats;
            public int Magazine, Reserve;
        }

        public struct ShotHit
        {
            public Collider Collider;
            public Vector3 Point, Normal;
            public float Distance;
            public WeaponStats Weapon;
        }

        public Material tracerMaterial;
        public float tracerSeconds = 0.05f;

        /// <summary>Slots: 0 primary, 1 sidearm, 2 knife (null = empty).</summary>
        public readonly Gun[] Slots = new Gun[3];
        public int Current { get; private set; } = 2;
        public Gun Equipped => Slots[Current];
        public bool Reloading => _reloadEnd > Time.time;
        public float ReloadProgress => Reloading ? 1f - (_reloadEnd - Time.time) / Mathf.Max(0.01f, Equipped.Stats.ReloadTime) : 0f;
        public bool InputEnabled { get; set; } = true;
        /// <summary>Current spread in degrees (for the crosshair).</summary>
        public float CurrentSpread { get; private set; }

        /// <summary>Every pellet that hit something (world or player collider).</summary>
        public event Action<ShotHit> Hit;
        /// <summary>A trigger pull (for HUD / ammo updates).</summary>
        public event Action Fired;

        private FpsController _fps;
        private float _nextShot, _reloadEnd, _equipEnd, _bloom;
        private readonly List<(LineRenderer Line, float Until)> _tracers = new List<(LineRenderer, float)>();
        private readonly Queue<Transform> _impacts = new Queue<Transform>();
        private Collider _self;

        private void Awake()
        {
            _fps = GetComponent<FpsController>();
            _self = GetComponent<Collider>();
        }

        /// <summary>Loadout from the replay: primary/sidearm classes, which one is in hand, and its ammo (255 = unknown).</summary>
        public void Setup(string primary, string sidearm, string inHand, int ammo, int reserve)
        {
            Slots[0] = Make(primary, false);
            Slots[1] = Make(sidearm, true);
            Slots[2] = new Gun { Stats = WeaponCatalog.Knife };
            int slot = 2;
            for (int i = 0; i < 2; i++)
                if (Slots[i] != null && Slots[i].Stats.Class == inHand) slot = i;
            if (slot < 2)
            {
                if (ammo != 255) Slots[slot].Magazine = Mathf.Min(ammo, Slots[slot].Stats.Magazine);
                if (reserve < 254) Slots[slot].Reserve = reserve;
            }
            else if (inHand != WeaponCatalog.KnifeClass) slot = Slots[0] != null ? 0 : Slots[1] != null ? 1 : 2;   // ability in hand
            Current = slot;
            _equipEnd = 0f;
        }

        private static Gun Make(string cls, bool sidearm)
        {
            var s = WeaponCatalog.Get(cls);
            if (s == null) return null;
            return new Gun { Stats = s, Magazine = s.Magazine, Reserve = s.Reserve };
        }

        private void Update()
        {
            UpdateTracers();
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (!InputEnabled || kb == null || mouse == null) { UpdateSpread(); return; }

            if (kb.digit1Key.wasPressedThisFrame) Equip(0);
            if (kb.digit2Key.wasPressedThisFrame) Equip(1);
            if (kb.digit3Key.wasPressedThisFrame) Equip(2);
            if (mouse.scroll.ReadValue().y > 0.1f) Cycle(-1);
            else if (mouse.scroll.ReadValue().y < -0.1f) Cycle(1);

            var gun = Equipped;
            if (kb.rKey.wasPressedThisFrame) StartReload();
            if (Reloading || Time.time < _equipEnd) { UpdateSpread(); return; }
            if (_reloadEnd > 0f) FinishReload();

            bool trigger = gun.Stats.Automatic ? mouse.leftButton.isPressed : mouse.leftButton.wasPressedThisFrame;
            if (trigger && Time.time >= _nextShot)
            {
                if (gun.Stats.Melee) Melee(gun.Stats);
                else if (gun.Magazine > 0) Fire(gun);
                else StartReload();
            }
            UpdateSpread();
        }

        public void Equip(int slot)
        {
            if (slot == Current || Slots[slot] == null) return;
            _reloadEnd = 0f;
            Current = slot;
            _equipEnd = Time.time + Slots[slot].Stats.EquipTime;
            _bloom = 0f;
        }

        private void Cycle(int dir)
        {
            for (int i = 1; i <= 3; i++)
            {
                int s = ((Current + dir * i) % 3 + 3) % 3;
                if (Slots[s] != null) { Equip(s); return; }
            }
        }

        private void StartReload()
        {
            var gun = Equipped;
            if (gun.Stats.Melee || Reloading || gun.Magazine >= gun.Stats.Magazine || gun.Reserve <= 0) return;
            _reloadEnd = Time.time + gun.Stats.ReloadTime;
        }

        private void FinishReload()
        {
            _reloadEnd = 0f;
            var gun = Equipped;
            int take = Mathf.Min(gun.Stats.Magazine - gun.Magazine, gun.Reserve);
            gun.Magazine += take;
            gun.Reserve -= take;
        }

        private void UpdateSpread()
        {
            var s = Equipped.Stats;
            _bloom = Mathf.MoveTowards(_bloom, 0f, Time.deltaTime * s.MaxSpread * 2.5f);
            float move = _fps != null ? Mathf.Clamp01(_fps.MoveFraction) : 0f;
            float moveSpread = _fps != null && !_fps.Grounded ? s.MoveSpread * 2f : _fps != null && (_fps.Walking || _fps.Crouching) ? move * s.MoveSpread * 0.35f : move * s.MoveSpread;
            float crouchBonus = _fps != null && _fps.Crouching ? 0.8f : 1f;
            CurrentSpread = (s.Spread + _bloom) * crouchBonus + moveSpread;
        }

        private void Fire(Gun gun)
        {
            var s = gun.Stats;
            _nextShot = Time.time + 1f / s.FireRate;
            if (!GameSettings.Current.infiniteAmmo) gun.Magazine--;
            var cam = _fps.Camera.transform;
            for (int p = 0; p < s.Pellets; p++)
            {
                var dir = Spread(cam, CurrentSpread);
                Trace(cam.position, dir, s);
            }
            _bloom = Mathf.Min(s.MaxSpread, _bloom + s.SpreadPerShot);
            _fps.AddRecoil(s.Recoil, Random.Range(-0.15f, 0.15f) * s.Recoil);
            Fired?.Invoke();
        }

        private void Melee(WeaponStats s)
        {
            _nextShot = Time.time + 1f / s.FireRate;
            var cam = _fps.Camera.transform;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, s.Range, ~0, QueryTriggerInteraction.Ignore) && hit.collider != _self)
                Hit?.Invoke(new ShotHit { Collider = hit.collider, Point = hit.point, Normal = hit.normal, Distance = hit.distance, Weapon = s });
            Fired?.Invoke();
        }

        private static Vector3 Spread(Transform cam, float degrees)
        {
            if (degrees <= 0f) return cam.forward;
            var r = Random.insideUnitCircle * Mathf.Tan(degrees * Mathf.Deg2Rad);
            return (cam.forward + cam.right * r.x + cam.up * r.y).normalized;
        }

        private void Trace(Vector3 origin, Vector3 dir, WeaponStats s)
        {
            var end = origin + dir * s.Range;
            var hits = Physics.RaycastAll(origin, dir, s.Range, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider == _self) continue;
                end = hit.point;
                Hit?.Invoke(new ShotHit { Collider = hit.collider, Point = hit.point, Normal = hit.normal, Distance = hit.distance, Weapon = s });
                Impact(hit.point, hit.normal);
                break;
            }
            // tracer from a little below-right of the eye, like a gun barrel
            var cam = _fps.Camera.transform;
            ShowTracer(cam.position + cam.right * 0.12f - cam.up * 0.1f + cam.forward * 0.3f, end);
        }

        private void ShowTracer(Vector3 from, Vector3 to)
        {
            LineRenderer line = null;
            for (int i = 0; i < _tracers.Count; i++)
                if (!_tracers[i].Line.gameObject.activeSelf) { line = _tracers[i].Line; _tracers.RemoveAt(i); break; }
            if (line == null)
            {
                line = new GameObject("Tracer").AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.widthMultiplier = 0.015f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (tracerMaterial != null) line.sharedMaterial = tracerMaterial;
                line.startColor = new Color(1f, 0.95f, 0.7f, 0.9f);
                line.endColor = new Color(1f, 0.95f, 0.7f, 0.2f);
            }
            line.gameObject.SetActive(true);
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            _tracers.Add((line, Time.time + tracerSeconds));
        }

        private void UpdateTracers()
        {
            foreach (var (line, until) in _tracers)
                if (line.gameObject.activeSelf && Time.time > until) line.gameObject.SetActive(false);
        }

        private void Impact(Vector3 point, Vector3 normal)
        {
            Transform t;
            if (_impacts.Count >= 64) t = _impacts.Dequeue();
            else
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "Impact";
                Destroy(go.GetComponent<Collider>());
                go.GetComponent<Renderer>().material.color = new Color(0.08f, 0.08f, 0.08f);
                t = go.transform;
                t.localScale = Vector3.one * 0.06f;
            }
            t.position = point + normal * 0.005f;
            t.rotation = Quaternion.LookRotation(-normal);
            _impacts.Enqueue(t);
        }
    }
}
