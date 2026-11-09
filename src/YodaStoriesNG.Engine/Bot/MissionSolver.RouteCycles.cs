using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public partial class MissionSolver
{
    private readonly Dictionary<int,(int X,int Y,string State)> _departedAreas=new();
    private readonly Dictionary<(int From,int To,int X,int Y),(string From,string To,int Returns)> _unchangedPortalCycles=new();
    private readonly Dictionary<(int From,int To),(string Progress,int Returns)> _unchangedZoneCycles=new();
    private readonly Dictionary<(int Zone,int X,int Y),string> _failedDoorStates=new();
    private readonly Dictionary<(int Zone,int Action),string> _failedScriptStates=new();
    private readonly Dictionary<(int Zone,int Action),string> _failedScriptSemanticStates=new();
    private readonly Dictionary<(int Zone,int Action),string> _completedScriptStates=new();
    private readonly Dictionary<(int Zone,int Action,string State),int> _unchangedScriptAttempts=new();
    private readonly Dictionary<(int Zone,int X,int Y),string> _failedPassageStates=new();
    private readonly Dictionary<(int Zone,int X,int Y),string> _failedPassageSemanticStates=new();
    private readonly Dictionary<(int Zone,int X,int Y,string State),int> _unchangedPassageAttempts=new();
    private readonly HashSet<(int TargetZone,int TargetX,int TargetY,int Zone,int X,int Y,string State)> _unreachableRouteTiles=new();
    private readonly HashSet<(int TargetZone,int TargetX,int TargetY,int Zone,int Action,string State)> _unreachableRouteActions=new();

    private string RouteState(int zoneId) =>
        $"{BlockPuzzlePlanner.Layout(_state.CurrentZoneId==zoneId && _state.CurrentZone is { } current ? current : _gameData.Zones[zoneId])}:"+
        $"{_state.GetCounter(zoneId)}:{_state.GetSectorCounter(zoneId)}:"+
        $"{_state.ZoneRandom.GetValueOrDefault(zoneId)}:{_state.DisabledActions.Count}:{_state.DefeatedMonsters.Count}:"+
        string.Join(',',_state.Inventory.OrderBy(i=>i))+":vars="+string.Join(',',_state.Variables.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))+":needs="+
        string.Join(',',_neededNativeItems.OrderBy(i=>i));

    private string RouteResolutionState(int zoneId) =>
        $"{BlockPuzzlePlanner.Layout(_state.CurrentZoneId==zoneId && _state.CurrentZone is { } current ? current : _gameData.Zones[zoneId])}:"+
        $"{_state.GetCounter(zoneId)}:{_state.GetSectorCounter(zoneId)}:{_state.ZoneRandom.GetValueOrDefault(zoneId)}:"+
        $"disabled={string.Join(',',_state.DisabledActions.Where(a=>a.Zone==zoneId).Select(a=>a.Action).OrderBy(i=>i))}:"+
        $"defeated={string.Join(',',_state.DefeatedMonsters.Where(m=>m.StartsWith($"{zoneId}_",StringComparison.Ordinal)).OrderBy(m=>m))}:"+
        $"inventory={string.Join(',',_state.Inventory.OrderBy(i=>i))}:vars={string.Join(',',_state.Variables.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))}";

    internal string CaptureRouteState(int zoneId) => RouteState(zoneId);

    internal void MarkDoorFailed(int x,int y,string observedState) =>
        _failedDoorStates[(_state.CurrentZoneId,x,y)]=observedState;

    internal void MarkScriptFailed(int zone,int action,string observedState)
    {
        _failedScriptStates[(zone,action)]=observedState;
        _failedScriptSemanticStates[(zone,action)]=CapturePassageSemanticState(zone);
    }

    internal bool RecordUnchangedScriptAttempt(int zone,int action,string semanticState)
    {
        var key=(zone,action,semanticState);
        int attempts=_unchangedScriptAttempts.GetValueOrDefault(key)+1;
        _unchangedScriptAttempts[key]=attempts;
        return attempts>=2;
    }

    internal void MarkPassageFailed(int zone,int x,int y,string observedState)
    {
        _failedPassageStates[(zone,x,y)]=observedState;
        _failedPassageSemanticStates[(zone,x,y)]=CapturePassageSemanticState(zone);
    }

    internal string CapturePassageProgressState(int zone) => PassageProgressState(zone);

    internal string CapturePassageSemanticState(int zone) =>
        $"{_worldGenerator.CurrentWorld?.Mission?.CurrentStep}:{_worldGenerator.CurrentWorld?.Mission?.IsCompleted}:"+
        $"{string.Join(',',_state.Inventory.OrderBy(i=>i))}:map="+
        BlockPuzzlePlanner.Layout(_state.CurrentZoneId==zone && _state.CurrentZone is { } current ? current : _gameData.Zones[zone])+":actions="+
        string.Join(',',_state.DisabledActions.Where(a=>a.Zone==zone).OrderBy(a=>a.Action).Select(a=>a.Action));

    internal bool RecordUnchangedPassageAttempt(int zone,int x,int y,string semanticState)
    {
        var key=(zone,x,y,semanticState);
        int attempts=_unchangedPassageAttempts.GetValueOrDefault(key)+1;
        _unchangedPassageAttempts[key]=attempts;
        return attempts>=2;
    }

    private string PassageProgressState(int zone) =>
        $"{_worldGenerator.CurrentWorld?.Mission?.CurrentStep}:{_worldGenerator.CurrentWorld?.Mission?.IsCompleted}:"+
        $"{string.Join(',',_state.Inventory.OrderBy(i=>i))}:counter={_state.GetCounter(zone)}:"+
        $"sector={_state.GetSectorCounter(zone)}:actions="+
        string.Join(',',_state.DisabledActions.Where(a=>a.Zone==zone).OrderBy(a=>a.Action).Select(a=>a.Action));

    internal bool IsScriptFailedWithoutProgress(int zone,int action) =>
        _failedScriptStates.TryGetValue((zone,action),out var failedState) && failedState==RouteState(zone) ||
        _failedScriptSemanticStates.TryGetValue((zone,action),out var failedSemantic) &&
            failedSemantic==CapturePassageSemanticState(zone);

    // A script may legitimately update only its random/counter state or show
    // dialogue without disabling itself. Once the bot has executed that action
    // in the current state, do not select it again until the area changes.
    internal void MarkScriptCompleted(int zone,int action) =>
        _completedScriptStates[(zone,action)]=RouteState(zone);

    internal bool IsScriptCompletedInCurrentState(int zone,int action) =>
        _completedScriptStates.TryGetValue((zone,action),out var completed) && completed==RouteState(zone);

    /// <summary>Choose a reachable, unexplored adjacent area when the current mission target has no route.</summary>
    internal BotObjective? GetExplorationRecoveryObjective()
    {
        var graph=ActionGraph;
        var zone=_state.CurrentZone;
        if(zone==null) return null;
        var reachable=WorldActionGraph.WalkableArea(_gameData,zone,_state.PlayerX,_state.PlayerY,_state,_canWalk);
        var sector=_worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(zone.Id);
        // A mission objective can be physically unreachable from the current
        // component while a scripted, same-zone passage is usable from here.
        // Route through that authored action before falling back to world
        // exploration (for example, an item-triggered hatch between rooms).
        var localPassages=ScriptedPassage.Instructions(zone,_state,sector,_gameData.GameType).ToArray();
        foreach(var passage in localPassages
            .Where(p=>p.RequiredItemId is int item && _state.HasItem(item) &&
                (!_failedPassageStates.TryGetValue((zone.Id,p.TargetX,p.TargetY),out var failed) ||
                    failed!=PassageProgressState(zone.Id)) &&
                (!_failedPassageSemanticStates.TryGetValue((zone.Id,p.TargetX,p.TargetY),out var failedSemantic) ||
                    failedSemantic!=CapturePassageSemanticState(zone.Id)))
            .OrderBy(p=>Math.Abs((p.ArrivalX ?? p.TargetX)-_state.PlayerX)+
                Math.Abs((p.ArrivalY ?? p.TargetY)-_state.PlayerY)))
        {
            bool ready=passage.PassageRequiresBump
                ? Math.Abs(_state.PlayerX-passage.TargetX)+Math.Abs(_state.PlayerY-passage.TargetY)==1 ||
                    reachable.Any(c=>Math.Abs(c.X-passage.TargetX)+Math.Abs(c.Y-passage.TargetY)==1)
                : reachable.Contains((passage.TargetX,passage.TargetY));
            if(!ready) continue;
            Console.WriteLine($"[BOT] Recovery passage: use item {passage.RequiredItemId} at ({passage.TargetX},{passage.TargetY}) to reach ({passage.ArrivalX},{passage.ArrivalY}) in zone {zone.Id}");
            return passage;
        }
        if(graph==null) return null;
        foreach(var edge in graph.Edges.Where(e=>e.From==zone.Id &&
                (e.To!=zone.Id || e.Instruction.Type==ObjectiveType.TraversePassage)))
            if(!RouteEdgeAllowed(edge))
                Console.WriteLine($"[BOT] Recovery edge {edge.From}->{edge.To} ({edge.Instruction.Type} {edge.Instruction.Description}) excluded by failed-action or unchanged-cycle guard");
        foreach(var edge in graph.Edges.Where(e=>e.From==zone.Id &&
                (e.To!=zone.Id || e.Instruction.Type==ObjectiveType.TraversePassage) && RouteEdgeAllowed(e))
            .OrderBy(e=>_visitedZones.Contains(e.To) ? 1 : 0)
            .ThenBy(e=>e.Instruction.Type is ObjectiveType.EnterDoor or ObjectiveType.TraversePassage ? 0 : 1)
            .ThenBy(e=>e.To))
        {
            var crossings=graph.Crossings(edge,reachable).ToArray();
            if(crossings.Length==0)
            {
                Console.WriteLine($"[BOT] Recovery edge {edge.From}->{edge.To} has no walkable crossing from ({_state.PlayerX},{_state.PlayerY}); component has {reachable.Count} cells");
                continue;
            }
            var crossing=crossings[0];
            var objective=edge.Instruction;
            if(objective.Type==ObjectiveType.ChangeZone)
            {
                objective=new BotObjective { Type=ObjectiveType.ChangeZone,TargetZoneId=edge.To,
                    Direction=objective.Direction,CrossingX=crossing.From.X,CrossingY=crossing.From.Y,
                    TargetX=crossing.From.X,TargetY=crossing.From.Y,
                    Description=$"Explore zone {edge.To} via {objective.Direction} from zone {zone.Id}" };
            }
            Console.WriteLine($"[BOT] Recovery exploration: taking reachable route {zone.Id} -> {edge.To}");
            return objective;
        }
        return null;
    }

    internal void ObserveRouteTransition(int from,int x,int y,BotObjective? instruction)
    {
        int to=_state.CurrentZoneId;
        if(from<0 || from>=_gameData.Zones.Count || from==to) return;
        string progress=RouteProgressState();
        var zoneKey=(from,to);
        int zoneReturns=_unchangedZoneCycles.TryGetValue(zoneKey,out var priorZone) &&
            priorZone.Progress==progress
            ? priorZone.Returns+1 : 1;
        _unchangedZoneCycles[zoneKey]=(progress,zoneReturns);
        if(zoneReturns==2)
            Console.WriteLine($"[BOT] Repeated unchanged transition {from}->{to}; excluding it while the area state stays the same");
        if(instruction!=null && instruction.Type!=ObjectiveType.ChangeZone &&
            _departedAreas.TryGetValue(to,out var previous) && previous.State==RouteState(to) &&
            WorldActionGraph.WalkableArea(_gameData,_gameData.Zones[to],_state.PlayerX,_state.PlayerY,_state,_canWalk)
                .Contains((previous.X,previous.Y)))
        {
            var key=(from,to,instruction.TargetX,instruction.TargetY);
            string source=RouteState(from),destination=RouteState(to);
            int returns=_unchangedPortalCycles.TryGetValue(key,out var prior) &&
                prior.From==source && prior.To==destination ? prior.Returns+1 : 1;
            _unchangedPortalCycles[key]=(source,destination,returns);
            if(returns>=2)
                Console.WriteLine($"[BOT] Portal {from} ({key.TargetX},{key.TargetY}) -> {to} repeatedly returned to an unchanged area; trying another entrance");
        }
        _departedAreas[from]=(x,y,RouteState(from));
    }

    private bool IsUnchangedPortalCycle(int from,int to,int x,int y) =>
        _unchangedPortalCycles.TryGetValue((from,to,x,y),out var saved) &&
        saved.Returns>=2 && saved.From==RouteState(from) && saved.To==RouteState(to);

    private bool IsUnchangedZoneCycle(int from,int to) =>
        _unchangedZoneCycles.TryGetValue((from,to),out var saved) && saved.Returns>=2 &&
        saved.Progress==RouteProgressState();

    private string RouteProgressState() =>
        $"{string.Join(',',_state.Inventory.OrderBy(i=>i))}|"+
        $"{_worldGenerator.CurrentWorld?.Mission?.CurrentStep}:{_worldGenerator.CurrentWorld?.Mission?.IsCompleted}|"+
        $"needed={string.Join(',',_neededNativeItems.OrderBy(i=>i))}";

    internal bool RouteEdgeAllowed(WorldActionGraph.Edge edge) =>
        !(edge.Instruction.Type==ObjectiveType.TraversePassage &&
            _failedPassageStates.TryGetValue((edge.From,edge.Instruction.TargetX,edge.Instruction.TargetY),out var failedPassageState) &&
            failedPassageState==PassageProgressState(edge.From)) &&
        !(edge.Instruction.Type==ObjectiveType.TraversePassage &&
            _failedPassageSemanticStates.TryGetValue((edge.From,edge.Instruction.TargetX,edge.Instruction.TargetY),out var failedPassageSemanticState) &&
            failedPassageSemanticState==CapturePassageSemanticState(edge.From)) &&
        !(edge.Instruction.Type==ObjectiveType.EnterDoor &&
            _failedDoorStates.TryGetValue((edge.From,edge.Instruction.TargetX,edge.Instruction.TargetY),out var failedState) &&
            failedState==RouteState(edge.From)) &&
        (!edge.Instruction.Direction.HasValue || !IsExitBlocked(edge.From,edge.Instruction.Direction.Value)) &&
        (!IsUnchangedZoneCycle(edge.From,edge.To) || IsReturnTravelEdge(edge.From,edge.To) || IsRoomReturnEdge(edge.From,edge.To)) &&
        // Reject repeated entry into a dead end, while preserving its real exit.
        // A return doorway must remain usable to reach another mission branch.
        (edge.Instruction.Type==ObjectiveType.EnterDoor &&
            _roomReturns.TryGetValue(edge.From,out int parent) && parent==edge.To ||
         !IsUnchangedPortalCycle(edge.From,edge.To,edge.Instruction.TargetX,edge.Instruction.TargetY));

    private bool IsReturnTravelEdge(int from,int to) =>
        _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(from)?.Type==ZoneType.TravelEnd &&
        _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(to)?.Type==ZoneType.TravelStart;

    private bool IsRoomReturnEdge(int from,int to) =>
        _roomReturns.TryGetValue(from,out int parent) && parent==to;

    private bool IsUnchangedPortalAction(Zone zone,Data.Action action)
    {
        var transfer=action.Instructions.FirstOrDefault(i=>i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3);
        var trigger=action.Conditions.FirstOrDefault(c=>c.Arguments.Count>=2 && c.Opcode is
            ConditionOpcode.Bump or ConditionOpcode.StandingOn or ConditionOpcode.PlacedItemIs);
        return transfer!=null && trigger!=null &&
            IsUnchangedPortalCycle(zone.Id,transfer.Arguments[0],trigger.Arguments[0],trigger.Arguments[1]);
    }
}
