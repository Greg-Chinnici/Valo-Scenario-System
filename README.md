# ValoReplay

A Unity app for Valorant replays that you can step into rather than just watch. Pause a round at any moment, take over
any player and play the rest in first person, or hand every player to the AI and give orders. The other players are
an AI fitted to how the real players in that match played. It decides from what its team could know, not from where
everyone really is.

## A replay you can change

A recording only shows one way a round went. ValoReplay restarts the round from any moment as a simulation. Up to that
point everything is exactly as recorded, and you can change what happens next:

| Mode | What it does |
| --- | --- |
| **Watch** | Plays the recording. Follow any player, use the 3D map or the minimap, view aim lines, shot tracers and the scoreboard. |
| **Intel** | Shows what one team knows at this moment: last-seen positions and how sure the team still is of them, sounds, and areas enemies are probably in. The AI decides from this same knowledge. |
| **Play as** | Take over the followed player and play the rest of the round in first person. |
| **Command** | Every player becomes AI from this moment and takes your orders, MOBA style: move, plant or defuse, hold an angle, or hand a player back to the AI. |

When you take over, nobody else switches to AI straight away. Each player keeps following the recording until they
notice something the recording doesn't have. For example, they see or hear you, they get shot, or the enemy who killed
them in the recording is gone. From then on the AI plays them. So if you play the round as it went, it plays out as
recorded. If you play it differently, the change spreads only as far as the other players notice it.

## How the AI decides

Each AI player has a **profile** and a **team plan**, and makes a decision a few times a second.

**What it knows.** Each team has a knowledge blackboard: who was seen where, how long ago, what was heard, which
utility went where. Certainty fades with a 6-second half-life. When a seen enemy goes out of sight, the blackboard
keeps an estimate of the areas they are probably in. The AI builds a *situation* from this blackboard:
- the nearest believed threat, and how sure the team is of it;
- how many teammates and enemies are near;
- whether a trade is available;
- how far ahead of the team the agent is;
- its health and the clock.

It never reads where enemies really are. The Intel mode shows this same blackboard, so you can see why an agent did
what it did.

**What it can do.** The utility brain ([UtilityBrain.cs](Assets/ThirdParty/ValorantReplay.Sim/UtilityBrain.cs))
scores five actions:

| Action | Raised by |
| --- | --- |
| Hold | low aggression, a close and certain threat, being the waiting side in position, an Anchor play style |
| Advance | aggression, time pressure (attackers), a planted spike (defenders), distance to the objective; lowered by cohesion when ahead of the team |
| Peek | a believed enemy within 30 m that it can't see, aggression, a numbers advantage, skill, health |
| Fall back | low health, being outnumbered, low aggression, all scaled by how close and certain the threat is |
| Trade | a teammate who died nearby moments ago, cohesion, a Support play style |

The highest score wins. The current action gets a small bonus so agents don't change their minds on every decision.
Less skilled agents get more random noise in their scores, so they make worse reads. Aggression goes up with the
clock: `effective = aggression + (1 − aggression) · urgency`. A passive player still commits when time runs out.
Planting, defusing and picking up the spike happen separately once an agent reaches them.

**The team plan.** The team director ([TeamDirector.cs](Assets/ThirdParty/ValorantReplay.Sim/TeamDirector.cs)) only
tells each agent where its objective is. Attackers pick a site and when to execute, and rotate once if they learn the
site is stacked. Defenders split across sites and retake after a plant. Each agent still decides for itself whether
to go there.

## Learned from replays

None of the behaviour above is hand-tuned per player. It comes from the replays: the one you are playing for the
players in it, and every other replay of the same map for the map itself. The more replays you have of a map, the
better the AI plays it.

**Profiles** ([ProfileFitter.cs](Assets/ThirdParty/ValorantReplay.Sim/ProfileFitter.cs)). Each player in a match
gets a profile from what they did in it. The four style knobs are scored against a measured population of 170 player
matches. On each knob, 0.5 means typical:

| Knob | From |
| --- | --- |
| Skill | headshot rate and K/D |
| Aggression | how often they took the round's opening duel |
| Cohesion | how close they stayed to teammates (mostly) and how often their deaths were traded |
| Play style | Lurker if they played far from the team; otherwise Entry, Anchor or Support from their agent's role |

Each knob is `0.5 + 0.2 · z · trust`, where `z` is how far the player is from the population average and
`trust = rounds / (rounds + 8)`. A player seen for a few rounds stays near average; a full match moves the knobs further.

How a player shoots is measured directly
([Measurements.cs](Assets/ThirdParty/ValorantReplay.Sim/Measurements.cs)) with the same definitions the simulation
uses for its own agents:
- accuracy;
- head and leg share of hits;
- reaction time from first seeing an enemy to the first shot at them;
- crosshair placement (how far the view was from the enemy when they appeared).

A measured accuracy can't be used as a simulation parameter directly. Distance and movement lower what an agent
actually hits, and turning and waiting for the next shot add to reaction time. `vrpsim tune`
([ProfileTuner.cs](Assets/ThirdParty/ValorantReplay.Sim/ProfileTuner.cs)) works out the conversion by simulation.
After it, a fitted agent hits, reacts and places its crosshair like the player it replaces.

**Where players look** ([LookModel.cs](Assets/ThirdParty/ValorantReplay.Sim/LookModel.cs)). For every 3 m patch of a
map, by side and by whether the player is moving, there is a histogram of the directions real players looked during
fights. Agents check the angles real players checked, as often as they checked them, instead of looking where they
walk.

**When attacks go in** ([Tempo.cs](Assets/ThirdParty/ValorantReplay.Sim/Tempo.cs)). Execute and plant times are
learned from every replay of the map, so attackers on that map hit a site when real teams did.

**The map itself** ([SimMap.cs](Assets/ThirdParty/ValorantReplay.Sim/SimMap.cs)). The walkable grid, the sites and the
spawns are built from where players stood in the replays. The simulation needs no map geometry and runs the same
outside Unity.

Profiles are a handful of numbers that mean the same thing in every match: an Entry with high aggression from one
match plays like one in another. You can study a style across replays, put it in a round it never played, and see
what changes.

## How close it is

`vrpsim calibrate` checks the AI against what really happened
([Calibration.cs](Assets/ThirdParty/ValorantReplay.Sim/Calibration.cs)). It restarts every recorded round with every
player as AI, from the barrier drop and from later moments, runs each restart many times, and scores the real outcome
against the spread of simulated ones. On the 17 published replays (155 rounds, 424 restart points):

| | Recorded | Simulated |
| --- | --- | --- |
| Accuracy | 20% | 19% (36% from the knobs alone, without measured shooting); r = 0.81 across players |
| Reaction time | 0.46 s | 0.46 s; r = 0.82 |
| Crosshair placement error | 11.9° | 12.3°; r = 0.82 |
| Time to decide a round | 42.0 s | 43.0 s |
| Round ends: elimination / defused / time | 74% / 18% / 1% | 70% / 23% / 1% |

- Attacker win prediction: Brier score 0.220, against 0.238 without learned tempo.
- Kills per round per player: correlation 0.33–0.38 with the recorded numbers (0.10 with default profiles).

What still differs:
- Trades are too common (22–23% of deaths against 12%).
- Agents hold still where real players keep moving.
- Abilities, economy, recoil and jumping are not modelled yet.

## What's here

| Path | What |
| --- | --- |
| `Assets/` | The Unity project (Unity 6000.0.31f1, URP): replay viewer, round play, command mode, knowledge blackboard, player characters. |
| `Assets/ThirdParty/ValorantReplay.Playback` | The `.vrp` replay format: reader and writer, tags and highlights, agent and map catalogs. |
| `Assets/ThirdParty/ValorantReplay.Sim` | Round simulation and the AI: utility brain, team director, profile fitter, gunfights, learned look angles and tempo, calibration. |
| `sim/` | .NET projects over those same sources: tests and the `vrpsim` command line. |
| `data/replays/` | Anonymized `.vrp` replays (see its README). |
| `Tools/MapExport` | Blender script that exports a map for the in-editor map builder. |

A separate, private converter turns Valorant's own `.vrf` replays into `.vrp` files. It also removes everything that
identifies the players before a replay is published here.

## Getting started

1. Open the project in Unity 6000.0.31f1.
2. Maps: the 3D maps are not in the repository. Export them from your own map files with
   `Tools/MapExport/export_map.py`, then build them with **ValoReplay > Maps > Build**. Without them, replays play over
   the minimap. The AI doesn't need them.
3. Characters (optional): download a Mixamo character and rifle animations into `Assets/Characters/Mixamo`, then run
   **ValoReplay > Characters > Set Up Mixamo Character**. Without them, players use the built-in mannequin.
4. Play the `VrfImport` scene: it lists the replays in `data/replays`. Pick one and press **Watch Replay**. Press F1 in
   the viewer for shortcuts.

## The simulation outside Unity

Everything the AI does also runs headless, on the same source files:

```bash
dotnet test sim/ValoReplay.Sim.sln
```

```bash
dotnet run --project sim/src/vrpsim -- profiles data/replays/<match>.vrp
```

```bash
dotnet run --project sim/src/vrpsim -- run data/replays/<match>.vrp --at 5:0:30 --ai -n 200
```

```bash
dotnet run --project sim/src/vrpsim -- calibrate data/replays/*.vrp
```

```bash
dotnet run --project sim/src/vrpsim -- tune data/replays/*.vrp
```

```bash
dotnet run --project sim/src/vrpsim -- validate data/replays
```

- `profiles` prints each player's fitted knobs and measured shooting.
- `run` replays the rest of a round many times from a moment (round 5, 0:30 here) and reports how it ends. With
  `--ai` everyone is AI. With `--takeover <n>`, only player n (numbered as `profiles` lists them) starts as AI, and
  the others follow the recording until they notice the difference. With neither, every run reproduces the recording.
- `calibrate` and `tune` are described above.
- `validate` checks that replays are fit to publish.

## Status

No license has been chosen yet: you can read the code, but not reuse it. Valorant is a trademark of Riot Games; this
project is not affiliated with or endorsed by Riot Games.
