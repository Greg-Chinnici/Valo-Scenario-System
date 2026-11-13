using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Tests;

/// <summary>.vrp write/read round trip, compatibility checks, coordinate conversion and playback helpers.</summary>
public class VrpFormatTests
{
    /// <summary>A small replay that uses every section of the current format (Unreal space).</summary>
    internal static VrpReplay Sample()
    {
        var v = new VrpReplay
        {
            MatchId = "match-1", Map = "/Game/Maps/Jam/Jam", Build = "++Ares-Core+release-13.06", RecordedAtTicks = 638_000_000_000_000_000,
            Duration = 0.2f, TickRate = 20f, TickCount = 4
        };
        v.Players.Add(new Player { Puuid = "p0", Name = "Alpha#EUW", CrosshairProfile = "Imported Crosshair 1", AgentId = "a0", AgentClass = "Sprinter_PC_C", AgentName = "Neon", AgentRole = "Duelist", Team = 0, PlayerId = 1 });
        v.Players.Add(new Player { Puuid = "p1", AgentId = "a1", AgentClass = "Wraith_PC_C", AgentName = "Omen", AgentRole = "Controller", Team = 1, PlayerId = 2 });
        v.Teams.Add(new Team { InitialRole = TeamRole.Attacking, FinalWins = 1 });
        v.Teams.Add(new Team { InitialRole = TeamRole.Defending, FinalWins = 0 });
        v.Rounds.Add(new Round
        {
            Number = 0, StartTime = 0f, EndTime = 0.2f, AttackingTeam = 0, WinningTeam = 0, PlantTime = 0.05f, PlantX = 100f, PlantY = 200f, PlantZ = 30f,
            DefuseTime = -1f, EndReason = RoundEndReason.Detonated, Site = "C", Planter = 0, Defuser = 255,
            BarrierTime = 0.01f, TeamLoadoutValue = new[] { 4200, 3900 },
            Loadouts =
            {
                new PlayerLoadout { Player = 0, Primary = "AssaultRifle_AK", Sidearm = "BasePistol", Armor = ArmorType.Heavy, Credits = 150, RoundStartCredits = 4150 },
                new PlayerLoadout { Player = 1, Sidearm = "LugerPistol", Armor = ArmorType.Light, Credits = 300, RoundStartCredits = 1200 },
            }
        });
        v.Kills.Add(new Kill { Time = 0.1f, Killer = 0, Victim = 1, Round = 0, Weapon = "AssaultRifle_AK", Headshot = true });
        v.Entities.Add(new Entity
        {
            Class = "Projectile_Wraith_Q", SpawnTime = 0f, DespawnTime = 0.2f, X = 1f, Y = 2f, Z = 3f, Yaw = 90f, Owner = 1, Agent = "Wraith", Slot = "Q", ImpactTime = 0.1f,
            Path = { new PathPoint { Time = 0f, X = 1f, Y = 2f, Z = 3f }, new PathPoint { Time = 0.1f, X = 11f, Y = 22f, Z = 33f } }
        });
        v.Damage.Add(new DamageEvent { Time = 0.1f, Attacker = 0, Victim = 1, Damage = 160f, Weapon = "AssaultRifle_AK", Region = HitRegion.Head, Killed = true });
        v.Equippables.Add("AssaultRifle_AK");
        v.Shots.Add(new Shot { Time = 0.09f, Player = 0, Weapon = "AssaultRifle_AK", X = 5f, Y = 6f, Z = 7f, Yaw = 45f, Pitch = 10f, Projectiles = 1, AmmoRemaining = 24, Directions = { (0.7f, 0.7f, 0.1f) } });
        v.RoundStates.Add(new RoundStateSample { Time = 0f, Remaining = 100f, Phase = RoundPhase.Combat, Spike = SpikeState.Carried });
        v.RoundStates.Add(new RoundStateSample { Time = 0.05f, Remaining = 45f, Phase = RoundPhase.Combat, Spike = SpikeState.Planted });
        v.Events.Add(new MatchEvent { Time = 0.01f, Type = MatchEventType.UltimateUsed, Player = 0 });
        v.Events.Add(new MatchEvent { Time = 0.15f, Type = MatchEventType.UltimateEnded, Player = 0 });
        v.StatusEffects.Add(new StatusEffect { Time = 0.02f, Duration = 0.1f, Player = 1, SourcePlayer = 0, Type = StatusEffectType.Blind, Source = 0 });
        v.StatusEffects.Add(new StatusEffect { Time = 0.03f, Duration = 0f, Player = 0, SourcePlayer = 1, Type = StatusEffectType.Reveal, Name = "FXC_Hunter_Q_RevealTarget" });
        v.SpikeCarriers.Add(new SpikeCarrierSample { Time = 0f, Holder = 0 });
        v.SpikeCarriers.Add(new SpikeCarrierSample { Time = 0.05f, Holder = VrpReplay.HolderNone });
        v.DefuseAttempts.Add(new DefuseAttempt { StartTime = 0.1f, EndTime = 0.15f, Player = 1, ProgressStart = 3.5f, ProgressEnd = 5f, Defused = false });
        v.PlayerStats.Add(new PlayerStatSample { Time = 0f, Player = 0, Credits = 4150, RoundStartCredits = 4150, UltPoints = 2 });
        v.PlayerStats.Add(new PlayerStatSample { Time = 0f, Player = 1, Credits = 1200, RoundStartCredits = 1200 });
        v.PlayerStats.Add(new PlayerStatSample { Time = 0.005f, Player = 0, Credits = 150, RoundStartCredits = 4150, UltPoints = 2 });
        v.PlayerStats.Add(new PlayerStatSample { Time = 0.1f, Player = 0, Credits = 150, RoundStartCredits = 4150, Kills = 1, Score = 180, UltPoints = 3 });
        v.PlayerStats.Add(new PlayerStatSample { Time = 0.1f, Player = 1, Credits = 300, RoundStartCredits = 1200, Deaths = 1 });
        v.AbilityCharges.Add(new AbilityChargeSample { Time = 0f, Player = 1, Slot = "E", Ability = "Ability_Wraith_E_Smoke", Charges = 2, CooldownEnd = -1f });
        v.AbilityCharges.Add(new AbilityChargeSample { Time = 0.05f, Player = 1, Slot = "E", Ability = "Ability_Wraith_E_Smoke", Charges = 1, CooldownEnd = 0.15f });
        v.AbilityCharges.Add(new AbilityChargeSample { Time = 0.15f, Player = 1, Slot = "E", Ability = "Ability_Wraith_E_Smoke", Charges = 2, TemporaryCharges = 1, CooldownEnd = -1f });
        v.AbilityCasts.Add(new AbilityCast { Time = 0.02f, Player = 1, Slot = "Q", Round = 0, X = 1f, Y = 2f, Z = 3f });
        v.Highlights = new HighlightSettings { ClutchMin = 3, ClutchRequireWin = false, MultiKill = 4, MultiKillWindow = 6.5f, FirstBlood = true };
        v.Tags.Add(new ReplayTag { Time = 0.05f, Round = 0, Label = "Plant", Note = "site C", Categories = { "spike", "util" }, Anchor = "round=1 barrierTime=0.04" });
        v.Tags.Add(new ReplayTag { Time = 0.1f, Duration = 0.05f, Round = 0, Player = 0, Label = "First blood - Alpha#EUW", Source = TagSource.Auto, Kind = Highlights.FirstBlood });
        v.Frames = new Frame[v.TickCount * v.Players.Count];
        for (int t = 0; t < v.TickCount; t++)
            for (int p = 0; p < 2; p++)
                v.Frames[t * 2 + p] = new Frame
                {
                    X = 100f * t + p, Y = 50f * p, Z = 10f, YawCenti = (short)(1000 * t), PitchCenti = (short)(-200 * p),
                    Flags = FrameFlags.Alive | FrameFlags.HasData | (p == 1 ? FrameFlags.Blinded : 0), Health = 100, Armor = 50, Shield = 0,
                    Weapon = 1, Ammo = 25, ReserveAmmo = p == 0 ? Frame.InfiniteReserve : (byte)75
                };
        return v;
    }

    private static VrpReplay RoundTrip(VrpReplay v)
    {
        var ms = new MemoryStream();
        v.Save(ms);
        ms.Position = 0;
        return VrpReplay.Load(ms);
    }

    [Fact]
    public void SaveLoad_RoundTripsEverySection()
    {
        var a = Sample();
        var b = RoundTrip(a);
        Assert.Equal((a.MatchId, a.Map, a.Build, a.RecordedAtTicks, a.Duration, a.TickRate, a.TickCount, a.Space),
                     (b.MatchId, b.Map, b.Build, b.RecordedAtTicks, b.Duration, b.TickRate, b.TickCount, b.Space));
        Assert.Equal(a.Players.Select(p => (p.Puuid, p.Name, p.CrosshairProfile, p.AgentId, p.AgentClass, p.AgentName, p.AgentRole, p.Team, p.PlayerId)),
                     b.Players.Select(p => (p.Puuid, p.Name, p.CrosshairProfile, p.AgentId, p.AgentClass, p.AgentName, p.AgentRole, p.Team, p.PlayerId)));
        Assert.Equal(a.Teams.Select(t => (t.InitialRole, t.FinalWins)), b.Teams.Select(t => (t.InitialRole, t.FinalWins)));
        Assert.Equal(a.Rounds.Select(r => (r.Number, r.StartTime, r.EndTime, r.AttackingTeam, r.WinningTeam, r.PlantTime, r.PlantX, r.PlantY, r.PlantZ, r.DefuseTime, r.EndReason, r.Site, r.Planter, r.Defuser)),
                     b.Rounds.Select(r => (r.Number, r.StartTime, r.EndTime, r.AttackingTeam, r.WinningTeam, r.PlantTime, r.PlantX, r.PlantY, r.PlantZ, r.DefuseTime, r.EndReason, r.Site, r.Planter, r.Defuser)));
        Assert.Equal(a.Kills.Select(k => (k.Time, k.Killer, k.Victim, k.Round, k.Weapon, k.Headshot)), b.Kills.Select(k => (k.Time, k.Killer, k.Victim, k.Round, k.Weapon, k.Headshot)));
        Assert.Equal(a.Damage.Select(d => (d.Time, d.Attacker, d.Victim, d.Damage, d.Weapon, d.Region, d.Killed)), b.Damage.Select(d => (d.Time, d.Attacker, d.Victim, d.Damage, d.Weapon, d.Region, d.Killed)));
        Assert.Equal(a.Entities.Select(e => (e.Class, e.SpawnTime, e.DespawnTime, e.X, e.Y, e.Z, e.Yaw, e.Owner, e.Agent, e.Slot, e.ImpactTime, e.Path.Count)),
                     b.Entities.Select(e => (e.Class, e.SpawnTime, e.DespawnTime, e.X, e.Y, e.Z, e.Yaw, e.Owner, e.Agent, e.Slot, e.ImpactTime, e.Path.Count)));
        Assert.Equal(a.Entities[0].Path, b.Entities[0].Path);
        Assert.Equal(a.Equippables, b.Equippables);
        Assert.Equal(a.Shots.Select(s => (s.Time, s.Player, s.Weapon, s.X, s.Y, s.Z, s.Yaw, s.Pitch, s.Projectiles, s.AmmoRemaining)),
                     b.Shots.Select(s => (s.Time, s.Player, s.Weapon, s.X, s.Y, s.Z, s.Yaw, s.Pitch, s.Projectiles, s.AmmoRemaining)));
        Assert.Equal(a.Shots[0].Directions, b.Shots[0].Directions);
        Assert.Equal(a.RoundStates, b.RoundStates);
        Assert.Equal(a.Events.Select(e => (e.Time, e.Type, e.Player)), b.Events.Select(e => (e.Time, e.Type, e.Player)));
        Assert.Equal(a.StatusEffects.Select(s => (s.Time, s.Duration, s.Player, s.SourcePlayer, s.Type, s.Source, s.Name)),
                     b.StatusEffects.Select(s => (s.Time, s.Duration, s.Player, s.SourcePlayer, s.Type, s.Source, s.Name)));
        Assert.Equal(a.SpikeCarriers, b.SpikeCarriers);
        Assert.Equal(a.DefuseAttempts.Select(d => (d.StartTime, d.EndTime, d.Player, d.ProgressStart, d.ProgressEnd, d.Defused)),
                     b.DefuseAttempts.Select(d => (d.StartTime, d.EndTime, d.Player, d.ProgressStart, d.ProgressEnd, d.Defused)));
        Assert.Equal(a.AbilityCasts.Select(c => (c.Time, c.Player, c.Slot, c.Round, c.X, c.Y, c.Z)), b.AbilityCasts.Select(c => (c.Time, c.Player, c.Slot, c.Round, c.X, c.Y, c.Z)));
        Assert.Equal(a.PlayerStats, b.PlayerStats);
        Assert.Equal(a.AbilityCharges, b.AbilityCharges);
        Assert.Equal(a.Rounds.Select(r => (r.BarrierTime, string.Join(",", r.TeamLoadoutValue))), b.Rounds.Select(r => (r.BarrierTime, string.Join(",", r.TeamLoadoutValue))));
        Assert.Equal(a.Rounds.SelectMany(r => r.Loadouts).Select(l => (l.Player, l.Primary, l.Sidearm, l.Armor, l.Credits, l.RoundStartCredits)),
                     b.Rounds.SelectMany(r => r.Loadouts).Select(l => (l.Player, l.Primary, l.Sidearm, l.Armor, l.Credits, l.RoundStartCredits)));
        Assert.Equal(a.Tags.Select(t => (t.Time, t.Duration, t.Round, t.Player, t.Label, t.Note, string.Join(",", t.Categories), t.Source, t.Kind, t.Anchor)),
                     b.Tags.Select(t => (t.Time, t.Duration, t.Round, t.Player, t.Label, t.Note, string.Join(",", t.Categories), t.Source, t.Kind, t.Anchor)));
        Assert.Equal((a.Highlights.Enabled, a.Highlights.ClutchMin, a.Highlights.ClutchRequireWin, a.Highlights.Ace, a.Highlights.MultiKill, a.Highlights.MultiKillWindow, a.Highlights.FirstBlood),
                     (b.Highlights.Enabled, b.Highlights.ClutchMin, b.Highlights.ClutchRequireWin, b.Highlights.Ace, b.Highlights.MultiKill, b.Highlights.MultiKillWindow, b.Highlights.FirstBlood));
        Assert.Equal(a.Frames, b.Frames);
    }

    [Fact]
    public void Header_IsMagicThenVersion()
    {
        var ms = new MemoryStream();
        Sample().Save(ms);
        var bytes = ms.ToArray();
        Assert.Equal(VrpReplay.Magic, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(VrpReplay.FormatVersion, BitConverter.ToUInt16(bytes, 4));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Load_RejectsOtherLayouts(int offset)
    {
        // no upgrades: a file from an older or newer layout is rejected (convert the .vrf again)
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(VrpReplay.Magic);
        w.Write((ushort)(VrpReplay.FormatVersion + offset));
        ms.Position = 0;
        var e = Assert.Throws<InvalidDataException>(() => VrpReplay.Load(ms));
        Assert.Contains("convert the .vrf again", e.Message);
    }

    [Fact]
    public void Load_RejectsBadMagic() =>
        Assert.Throws<InvalidDataException>(() => VrpReplay.Load(new MemoryStream(new byte[] { 1, 2, 3, 4, 7, 0 })));

    [Fact]
    public void ConvertToUnitySpace_SwapsAxesScalesAndFlipsPitch()
    {
        var v = Sample();
        var f0 = v.Frames[1 * 2 + 1];
        v.ConvertToUnitySpace();
        Assert.Equal(CoordinateSpace.Unity, v.Space);
        var f = v.Frames[1 * 2 + 1];
        // Unity (x, y, z) = Unreal (Y, Z, X) / 100
        Assert.Equal(f0.Y / 100f, f.X, 4);
        Assert.Equal(f0.Z / 100f, f.Y, 4);
        Assert.Equal(f0.X / 100f, f.Z, 4);
        Assert.Equal(f0.YawCenti, f.YawCenti);
        Assert.Equal(-f0.PitchCenti, f.PitchCenti);
        var s = v.Shots[0];
        Assert.Equal((0.7f, 0.1f, 0.7f), s.Directions[0]);   // unit vector: axis swap only
        Assert.Equal(-10f, s.Pitch);
        var c = v.AbilityCasts[0];
        AssertNear((0.02f, 0.03f, 0.01f), (c.X, c.Y, c.Z));
        var p = v.Entities[0].Path[1];
        AssertNear((0.22f, 0.33f, 0.11f), (p.X, p.Y, p.Z));
        var r = v.Rounds[0];
        AssertNear((2f, 0.3f, 1f), (r.PlantX, r.PlantY, r.PlantZ));
    }

    private static void AssertNear((float X, float Y, float Z) expected, (float X, float Y, float Z) actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    [Fact]
    public void ConvertToUnitySpace_IsIdempotentAndPersisted()
    {
        var v = Sample();
        v.ConvertToUnitySpace();
        var x = v.Frames[5].X;
        v.ConvertToUnitySpace();
        Assert.Equal(x, v.Frames[5].X);
        Assert.Equal(CoordinateSpace.Unity, RoundTrip(v).Space);
    }

    [Fact]
    public void ToUnrealXY_InvertsTheUnityConversion()
    {
        var v = Sample();
        v.ConvertToUnitySpace();
        v.ToUnrealXY(v.Rounds[0].PlantX, v.Rounds[0].PlantY, v.Rounds[0].PlantZ, out float ux, out float uy);
        Assert.Equal(100f, ux, 3);
        Assert.Equal(200f, uy, 3);
    }

    [Fact]
    public void Sample_InterpolatesBetweenTicks()
    {
        var v = Sample();
        var f = v.Sample(0, 0.075f);          // halfway between tick 1 (x=100) and tick 2 (x=200)
        Assert.Equal(150f, f.X, 3);
        Assert.Equal(1500, f.YawCenti);
    }

    [Fact]
    public void RoundStateAt_CountsDown()
    {
        var v = Sample();
        var s = v.RoundStateAt(0.04f);
        Assert.Equal(RoundPhase.Combat, s.Phase);
        Assert.Equal(99.96f, s.Remaining, 3);
        Assert.Equal(SpikeState.Planted, v.RoundStateAt(0.06f).Spike);
    }

    [Fact]
    public void SpikeAndDefuseHelpers()
    {
        var v = Sample();
        Assert.Equal(0, v.SpikeHolderAt(0.01f));
        Assert.Equal(VrpReplay.HolderNone, v.SpikeHolderAt(0.1f));
        Assert.Null(v.DefuseAt(0.05f));
        var d = v.DefuseAt(0.125f);
        Assert.NotNull(d);
        Assert.Equal(4.25f, d.ProgressAt(0.125f), 3);
        Assert.Equal("C", v.PlantSite(v.Rounds[0]));
    }

    [Fact]
    public void StatusEffectHelpers()
    {
        var v = Sample();
        Assert.Single(v.StatusEffectsAt(1, 0.05f));
        Assert.Empty(v.StatusEffectsAt(1, 0.2f));
        var ping = v.StatusEffects[1];
        Assert.True(ping.IsPing);
        Assert.False(ping.ActiveAt(0.03f));
    }

    [Fact]
    public void PlayerNames_AreSuppliedByPuuid_AndFallBackToAgent()
    {
        var v = Sample();
        Assert.Equal("Alpha#EUW", v.Players[0].DisplayName);
        Assert.Equal("Omen", v.Players[1].DisplayName);       // no name known
        Assert.Equal(1, v.ApplyNames(new Dictionary<string, string> { ["p1"] = "Bravo#NA1", ["nobody"] = "x" }));
        Assert.Equal("Bravo#NA1", v.Players[1].DisplayName);
        Assert.Equal("Bravo#NA1", RoundTrip(v).Players[1].Name);
    }

    [Fact]
    public void ScoreboardHelpers()
    {
        var v = Sample();
        Assert.Equal(-1f, v.StatsAt(0, -1f).Time);                      // nothing known yet
        Assert.Equal(4150, v.StatsAt(0, 0.001f).Credits);
        Assert.Equal(150, v.StatsAt(0, 0.05f).Credits);                 // after buying
        var end = v.StatsAt(0, 1f);
        Assert.Equal((1, 0, 0, 180, 3), (end.Kills, end.Deaths, end.Assists, end.Score, end.UltPoints));
        Assert.Equal(1, v.StatsAt(1, 1f).Deaths);
        Assert.Equal(180f, v.AverageCombatScore(0, 1f));                // one round completed
        Assert.Equal("AssaultRifle_AK", v.LoadoutOf(0, 0).Primary);
        Assert.Equal("", v.LoadoutOf(0, 1).Primary);
        Assert.Null(v.LoadoutOf(5, 0));
    }

    [Fact]
    public void ChargesAt_TracksChargesAndCooldown()
    {
        var v = Sample();
        Assert.Equal(-1f, v.ChargesAt(1, "Q", 1f).Time);                  // no such series
        Assert.Equal(2, v.ChargesAt(1, "E", 0.01f).Charges);
        var mid = v.ChargesAt(1, "E", 0.1f);
        Assert.Equal((1, 0.15f), (mid.Charges, mid.CooldownEnd));          // cooling down until 0.15 s
        var end = v.ChargesAt(1, "E", 1f);
        Assert.Equal((2, 1, -1f), (end.Charges, end.TemporaryCharges, end.CooldownEnd));
    }

    [Fact]
    public void Frame_InfiniteReserve()
    {
        var v = Sample();
        Assert.True(v.Frames[0].HasInfiniteReserve);
        Assert.False(v.Frames[1].HasInfiniteReserve);
        Assert.Equal("AssaultRifle_AK", v.WeaponOf(v.Frames[0]));
    }
}
