ValorantReplay.Sim - round simulation and the character AI. This folder is the only copy: the .NET projects in sim/
(tests, vrpsim) compile these files. Run the tests with `dotnet test sim` after changing it.

Round/      the simulation itself. RoundSim is one class split by concern into partial files:
              RoundSim.cs             state, construction, the Step loop, round end
              RoundSim.Host.cs        what Unity drives: external agents, plant/defuse, Command orders
              RoundSim.Recording.cs   FromReplay, and replay-mode agents following the .vrp until they diverge
              RoundSim.Perception.cs  sight, hearing, callouts, belief queries
              RoundSim.Decisions.cs   each AI agent's tick: orders, or brain + director objective
              RoundSim.Movement.cs    formation spots, pathing, spacing, hold spots, glances
              RoundSim.Combat.cs      targets, reactions, flicks, hits, damage, kills
              RoundSim.Spike.cs       carrying, planting, defusing, detonating
            plus RoundSetup / SimOptions (inputs), SimEvent (outputs), SimRandom (seeded streams), and helpers.
Agents/     one player: SimAgent (state), Profile (who it is), UtilityBrain (what it does next), Knowledge
            (beliefs, the callout board), CalloutTrust, WeaponStats.
Strategy/   team play: TeamDirector runs one AttackPlan or DefensePlan per team per round.
Map/        what is known about a map: SimMap, NavGrid, SimSpace, and what players do there (HoldModel, LookModel,
            TempoModel).
Analysis/   measuring and fitting: Metrics, Measurements, Calibration, ProfileFitter, ProfileTuner, SimBatch.
Bots/       example bot profiles and lineup matches.

Adding a team plan
  1. Subclass AttackPlan (or DefensePlan); see FakeAttack and LateCallAttack for complete examples. An AttackPlan says
     when it executes (ExecuteTime) and can override Begin (round start), Update (every tick before the plant) and
     Objective (where each player goes); the base class already does the standard attack. Read the round and ask
     what the team believes through PlanContext. The spike (dropped spike, post-plant, saving) is handled for every
     plan by TeamDirector.
  2. Plans are templates: each round plays a Clone, so keep settings in fields and create per-round collections in Begin.
  3. Use it: new RoundSetup { Attack = new MyAttack() } for one round, or a mix for every round:
     new SimOptions { AttackPlans = new PlanPicker<AttackPlan> { { new DefaultAttack(), 3 }, { new MyAttack(), 1 } } }.
     Plans can live outside this assembly (e.g. in Unity scripts).
  4. Add it to Plans.Attacks() to pick it by name (vrpsim --attack, menus), and compare it with
     `vrpsim plans data/replays --attack default,myplan -n 12`.
  The simulation's constants are calibrated with DefaultAttack and DefaultDefense, which stay the default.
