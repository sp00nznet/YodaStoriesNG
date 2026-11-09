using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public enum LocalPuzzleGoalKind { AcquireItem, SolveSector, ReachCell, Win }
public enum LocalPuzzleStatus { Verified, AlreadySatisfied, NeedsLiveActors, Unsupported, SearchLimit, NoPlan, ReplayMismatch }
public sealed record LocalPuzzleGoal(LocalPuzzleGoalKind Kind, int Sector, int Item = -1, int Zone = -1, int X = -1, int Y = -1,
    bool ReturnToEntry = true);
public sealed record LocalPuzzleStep(int Zone, BotObjective Instruction, string Before, string After);
public sealed record LocalPuzzlePlan(int SchemaVersion, string DataFingerprint, LocalPuzzleGoal Goal,
    int[] Scope, string InitialState, LocalPuzzleStep[] Steps, bool ReplayVerified);
public sealed record LocalPuzzleRequirement(string Kind, int Zone, int Actor, int Character, int X, int Y,
    int Health, string Reason, BotObjective? AfterInstruction = null);
public sealed record LocalPuzzleResult(LocalPuzzleStatus Status, string Detail, int Expanded,
    double Seconds, LocalPuzzlePlan? Plan = null, LocalPuzzleRequirement[]? Deferred = null);

/// <summary>State contracts omit frame timers and UI selections, but retain gameplay inputs.</summary>
public static class LocalPuzzleState
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented=true,
        Converters={new JsonStringEnumConverter()} };

    public static string Signature(GameData data, GameState state, WorldMap world, IEnumerable<int> scope,
        (int X,int Y)? position=null, IReadOnlyDictionary<int,(int Zone,int X,int Y)>? returns=null)
    {
        var payload=new {
            state.CurrentZoneId,PlayerX=position?.X ?? state.PlayerX,PlayerY=position?.Y ?? state.PlayerY,state.Health,
            state.HeroVisible,state.IsGameWon,state.IsGameOver,state.SmoothMovementEnabled,state.PreviousZoneId,
            Inventory=state.Inventory.Order().ToArray(),Weapons=state.Weapons.Order().ToArray(),
            Ammo=state.WeaponAmmo.OrderBy(p=>p.Key).Select(p=>new {p.Key,p.Value.CurrentAmmo,p.Value.MaxAmmo}),
            Counters=state.Counters.OrderBy(p=>p.Key),Variables=state.Variables.OrderBy(p=>p.Key),
            Random=state.ZoneRandom.OrderBy(p=>p.Key),Rolls=state.ZoneRolls.OrderBy(p=>p.Key),
            Disabled=state.DisabledActions.OrderBy(p=>p.Zone).ThenBy(p=>p.Action).Select(p=>$"{p.Zone}:{p.Action}"),
            Defeated=state.DefeatedMonsters.Order(),DisabledMonsters=state.DisabledMonsters.Order(),
            Collected=state.CollectedObjects.Order(),Visited=state.VisitedZones.Order(),Solved=state.SolvedZones.Order(),
            Actors=state.ZoneNPCs.Select(n=>new {n.NativeMonsterIndex,n.CharacterId,n.X,n.Y,n.Direction,n.Health,
                n.IsEnabled,n.IsHostile,n.IsInvulnerable,n.HasGivenItem,n.IsReadyToCollect,n.Behavior,
                n.StartX,n.StartY,n.WanderRadius,n.CurrentWaypoint,
                Waypoints=n.Waypoints.Select(p=>new {p.X,p.Y}),n.DropsLoot,n.LootItemId,n.CarriedItemId,n.CarriedItemQuantity}),
            MissionStep=world.Mission?.CurrentStep,MissionCompleted=world.Mission?.IsCompleted,
            DoorReturns=returns?.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value.Zone}:{p.Value.X}:{p.Value.Y}").ToArray(),
            Areas=scope.Distinct().Order().Select(id=>new {Id=id,Layout=BlockPuzzlePlanner.Layout(data.Zones[id]),
                Type=data.Zones[id].Type,Objects=data.Zones[id].Objects.Select(h=>new {h.Type,h.X,h.Y,h.Argument,h.Enabled}),
                Monsters=data.Zones[id].Monsters.Select(m=>new {m.CharacterId,m.X,m.Y,m.Loot,m.DropsLoot,m.Waypoints}),
                Solved=world.Sectors.GetValueOrDefault(id)?.Solved})
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    public static int[] Scope(WorldMap world, int sector, GameData data)
    {
        var owner=world.Sectors.GetValueOrDefault(sector);
        var ids=world.Sectors.Where(p=>owner!=null && ReferenceEquals(p.Value,owner)).Select(p=>p.Key).ToHashSet();
        ids.Add(sector);
        bool changed;
        do
        {
            changed=false;
            foreach(var pair in world.RoomParents)
                if(ids.Contains(pair.Value) && (world.Sectors.GetValueOrDefault(pair.Key) is not { } assigned ||
                    owner!=null && ReferenceEquals(assigned,owner))) changed|=ids.Add(pair.Key);
        } while(changed);
        return ids.Where(id=>id>=0 && id<data.Zones.Count).Order().ToArray();
    }

    public static bool Satisfies(LocalPuzzleGoal goal, GameState state, WorldMap world) => goal.Kind switch {
        LocalPuzzleGoalKind.AcquireItem=>state.HasItem(goal.Item),
        LocalPuzzleGoalKind.SolveSector=>world.Sectors.GetValueOrDefault(goal.Sector)?.Solved==true || state.IsZoneSolved(goal.Sector),
        LocalPuzzleGoalKind.ReachCell=>state.CurrentZoneId==goal.Zone && state.PlayerX==goal.X && state.PlayerY==goal.Y,
        LocalPuzzleGoalKind.Win=>state.IsGameWon,
        _=>false };
}
