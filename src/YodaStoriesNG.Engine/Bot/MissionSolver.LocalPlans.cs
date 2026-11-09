using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Bot;

public partial class MissionSolver
{
    public Func<LocalPuzzleGoal,LocalPuzzleResult>? LocalPlanner { get; set; }
    private LocalPuzzlePlan? _localPuzzlePlan;
    private int _localPuzzleStep;
    private readonly HashSet<string> _localPuzzleFailures=new();

    private BotObjective? ContinueLocalPuzzle(WorldMap world)
    {
        if(_localPuzzlePlan is not {ReplayVerified:true} plan) return null;
        string state=LocalPuzzleState.Signature(_gameData,_state,world,plan.Scope,returns:_doorReturnPoints);
        while(_localPuzzleStep<plan.Steps.Length && state==plan.Steps[_localPuzzleStep].After) _localPuzzleStep++;
        if(_localPuzzleStep>=plan.Steps.Length)
        { _localPuzzlePlan=null;return null; }
        var step=plan.Steps[_localPuzzleStep];
        if(step.Before!=state || step.Zone!=_state.CurrentZoneId)
        {
            Console.WriteLine("[BOT] Local puzzle state changed; discarding its remaining replay steps");
            _localPuzzlePlan=null;return null;
        }
        return step.Instruction;
    }

    private BotObjective? TryLocalPuzzle(WorldMap world)
    {
        // Keep the deepest item dependency across room transitions. Returning
        // to the outer reward here would immediately leave its supply room.
        if(_itemRouteFocus is int focused && TryLocalItem(world,focused) is { } prerequisite)
            return prerequisite;
        if(LocalPlanner==null || world.Mission?.CurrentPuzzleStep is not {ZoneId:int zone} step ||
            !world.Sectors.TryGetValue(zone,out var sector) ||
            !ReferenceEquals(world.Sectors.GetValueOrDefault(_state.CurrentZoneId),sector)) return null;
        if(step.RequiredItemId>0 && !_state.HasItem(step.RequiredItemId) &&
            !_consumedInteractions.Contains((zone,step.RequiredItemId))) return null;
        var goal=sector.Type==ZoneType.Goal
            ? new LocalPuzzleGoal(LocalPuzzleGoalKind.Win,sector.Zone)
            : step.RewardItemId>0 ? new LocalPuzzleGoal(LocalPuzzleGoalKind.AcquireItem,sector.Zone,step.RewardItemId)
            : new LocalPuzzleGoal(LocalPuzzleGoalKind.SolveSector,sector.Zone);
        return TryLocalPuzzle(world,goal);
    }

    private BotObjective? TryLocalItem(WorldMap world,int item)
    {
        if(LocalPlanner==null || item<0 || _state.HasItem(item) ||
            !world.Sectors.TryGetValue(_state.CurrentZoneId,out var sector)) return null;
        var scope=LocalPuzzleState.Scope(world,sector.Zone,_gameData);
        bool supplied=sector.FindItem==item || sector.AdditionalGainItem==item || scope.Any(id=>
            _gameData.Zones[id].Objects.Any(h=>h.Argument==item && h.Type is
                ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.Trigger) ||
            _gameData.Zones[id].Actions.Any(a=>ProvidesReward(a,item,sector)));
        return supplied ? TryLocalPuzzle(world,new(LocalPuzzleGoalKind.AcquireItem,sector.Zone,item)) : null;
    }

    private BotObjective? TryLocalPuzzle(WorldMap world,LocalPuzzleGoal goal)
    {
        if(LocalPlanner==null) return null;
        if(LocalPuzzleState.Satisfies(goal,_state,world)) return null;
        var scope=LocalPuzzleState.Scope(world,goal.Sector,_gameData);
        // Reconsider failed local plans only after a puzzle state/component
        // change, not on every ordinary walking position in the same corridor.
        var cells=WorldActionGraph.WalkableArea(_gameData,_state.CurrentZone!,_state.PlayerX,_state.PlayerY,_state,_canWalk);
        var anchor=cells.OrderBy(c=>c.Y).ThenBy(c=>c.X).First();
        string key=$"{goal}:{LocalPuzzleState.Signature(_gameData,_state,world,scope,anchor,_doorReturnPoints)}";
        if(_localPuzzleFailures.Contains(key)) return null;
        var result=LocalPlanner(goal);
        if(result.Plan is not {SchemaVersion:1,ReplayVerified:true,Steps.Length:>0} plan || plan.Goal!=goal ||
            !plan.Scope.SequenceEqual(scope) || plan.InitialState!=LocalPuzzleState.Signature(_gameData,_state,world,scope,returns:_doorReturnPoints))
        {
            if(_localPuzzleFailures.Count>=256) _localPuzzleFailures.Clear();
            _localPuzzleFailures.Add(key);return null;
        }
        Console.WriteLine($"[BOT] Verified local puzzle plan: {plan.Steps.Length} steps for {goal.Kind} in sector {goal.Sector}");
        _localPuzzlePlan=plan;_localPuzzleStep=0;
        return ContinueLocalPuzzle(world);
    }
}
