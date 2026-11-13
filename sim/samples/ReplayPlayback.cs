#if UNITY_5_3_OR_NEWER
// Minimal example: loads a .vrp, spawns a capsule per player and plays/scrubs the match.
// Ability objects (smokes, flashes, mollies, walls...) are small spheres moving along their paths; shots are drawn as
// Scene-view tracers (Debug.DrawRay). Blinded players turn white, other debuffs (nearsight, concuss, suppress, ...)
// darken them; the spike carrier gets a small yellow cube overhead.
// Add to an empty GameObject, set VrpPath, press Play. Requires VrpReplay.cs, AgentCatalog.cs and MapCatalog.cs in the project.
using System.Collections.Generic;
using UnityEngine;
using ValorantReplay.Playback;

public class ReplayPlayback : MonoBehaviour
{
    public string VrpPath;
    public float Time_ = 0f;
    public float Speed = 1f;
    public bool Playing = true;
    [Tooltip("Jump to the start of this round on Start (-1 = keep Time_)")]
    public int StartRound = -1;
    [Tooltip("How long a shot tracer stays visible (replay seconds)")]
    public float TracerSeconds = 0.1f;

    private VrpReplay _replay;
    private Transform[] _players;
    private Renderer[] _renderers;
    private Color[] _teamColors;
    private readonly List<Transform> _abilityPool = new List<Transform>();
    private Transform _spike;

    private void Start()
    {
        _replay = VrpReplay.Load(VrpPath);
        if (StartRound >= 0 && StartRound < _replay.Rounds.Count) Time_ = _replay.Rounds[StartRound].StartTime;
        _players = new Transform[_replay.Players.Count];
        _renderers = new Renderer[_players.Length];
        _teamColors = new Color[_players.Length];
        for (int i = 0; i < _players.Length; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var p = _replay.Players[i];
            go.name = $"{p.AgentName} {p.Name}".Trim();
            go.transform.SetParent(transform, false);
            _players[i] = go.transform;
            _renderers[i] = go.GetComponent<Renderer>();
            _teamColors[i] = p.Team == 0 ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.3f, 0.5f, 0.9f);
        }
        var spike = GameObject.CreatePrimitive(PrimitiveType.Cube);
        spike.name = "Spike";
        spike.GetComponent<Renderer>().material.color = Color.yellow;
        _spike = spike.transform;
        _spike.localScale = Vector3.one * 0.25f;
    }

    private void Update()
    {
        if (_replay == null) return;
        if (Playing) Time_ = Mathf.Min(_replay.Duration, Time_ + Time.deltaTime * Speed);
        for (int i = 0; i < _players.Length; i++)
        {
            var f = _replay.Sample(i, Time_);
            // positions are the pawn's capsule centre; Unity capsule primitive is 2 m tall, centre-pivoted.
            // The Unity* helpers work for both Unity-space (vrfconv default) and Unreal-space files.
            _players[i].localPosition = _replay.UnityPosition(f);
            _players[i].localRotation = Quaternion.Euler(0f, f.Yaw, 0f);
            _players[i].localScale = new Vector3(1f, f.Crouching ? 0.7f : 1f, 1f);
            // f.Health / f.Armor / f.Shield, _replay.WeaponOf(f), f.Ammo, f.Walking, f.Airborne are there for HUDs
            bool debuffed = false;
            foreach (var s in _replay.StatusEffectsAt(i, Time_)) if (s.Type != StatusEffectType.Blind) { debuffed = true; break; }
            _renderers[i].material.color = (f.Flags & FrameFlags.Blinded) != 0 ? Color.white : debuffed ? _teamColors[i] * 0.4f : _teamColors[i];
            _renderers[i].enabled = f.Alive && (f.Flags & FrameFlags.OffMap) == 0;
        }
        byte holder = _replay.SpikeHolderAt(Time_);
        _spike.gameObject.SetActive(holder < _players.Length);
        if (holder < _players.Length) _spike.position = _players[holder].position + Vector3.up * 1.3f;
        DrawAbilities();
        DrawShots();
    }

    private void DrawAbilities()
    {
        int used = 0;
        foreach (var e in _replay.Entities)
        {
            if (e.Agent.Length == 0 || Time_ < e.SpawnTime || (e.DespawnTime >= 0 && Time_ >= e.DespawnTime)) continue;
            if (used == _abilityPool.Count)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * 0.4f;
                _abilityPool.Add(go.transform);
            }
            var t = _abilityPool[used++];
            e.PositionAt(Time_, out float x, out float y, out float z);
            t.localPosition = _replay.UnityPoint(x, y, z);
            t.name = e.Class;
            t.gameObject.SetActive(true);
        }
        for (int i = used; i < _abilityPool.Count; i++) _abilityPool[i].gameObject.SetActive(false);
    }

    private void DrawShots()
    {
        foreach (var s in _replay.Shots)
        {
            if (s.Time > Time_ || s.Time < Time_ - TracerSeconds) continue;
            var origin = transform.TransformPoint(_replay.UnityPoint(s.X, s.Y, s.Z));
            foreach (var d in s.Directions)
            {
                var dir = transform.TransformDirection(_replay.UnityDirection(d.X, d.Y, d.Z));
                Debug.DrawRay(origin, dir * 30f, Color.yellow);
            }
        }
    }
}
#endif
