# ValoReplay

A Unity app for Valorant replays: watch a match from any angle, take over any player at any moment and play the rest of
the round in first person against an AI that only acts on what its team could know.

## What's here

| Path | What |
| --- | --- |
| `Assets/` | The Unity project (Unity 6000.0.31f1, URP): replay viewer, round play, knowledge blackboard, character AI, player characters. |
| `Assets/ThirdParty/ValorantReplay.Playback` | The `.vrp` replay format: reader and writer, tags and highlights, agent and map catalogs. |
| `Assets/ThirdParty/ValorantReplay.Sim` | Round simulation and the character AI: utility brain, team director, gunfights, learned tempo and look angles, calibration. |
| `sim/` | .NET projects over those same sources: tests, and `vrpsim` (simulate rounds, calibrate the AI against real rounds, validate replays). |
| `data/replays/` | Anonymized `.vrp` replays (see its README). |
| `Tools/MapExport` | Blender script that exports a map for the in-editor map builder. |

The `.vrp` files are converted from Valorant's own `.vrf` replays by a separate, private converter, which also removes
everything that identifies the players before a replay is published here.

## Getting started

1. Open the project in Unity 6000.0.31f1.
2. Maps: the 3D maps are not in the repository. Export them with `Tools/MapExport/export_map.py` from your own map files,
   then build them with **ValoReplay > Maps > Build**. Without them, replays play over the minimap.
3. Characters (optional): download a Mixamo character and rifle animations into `Assets/Characters/Mixamo`, then run
   **ValoReplay > Characters > Set Up Mixamo Character**. Without them, players use the built-in mannequin.
4. Play the `VrfImport` scene: it lists `data/replays`. Pick a replay and press **Open**.

## The simulation outside Unity

```bash
dotnet test sim/ValoReplay.Sim.sln
dotnet run --project sim/src/vrpsim -- calibrate data/replays/*.vrp
dotnet run --project sim/src/vrpsim -- validate data/replays
```

## Status

No license has been chosen yet: you can read the code, but not reuse it. Valorant is a trademark of Riot Games; this
project is not affiliated with or endorsed by Riot Games.
