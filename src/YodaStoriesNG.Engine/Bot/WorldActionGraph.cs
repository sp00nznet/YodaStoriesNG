using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Live world topology with executable instructions on each travel edge.</summary>
public sealed partial class WorldActionGraph
{
    public sealed record Edge(int From, int To, BotObjective Instruction);
    public sealed record Dependency(int SourceZone, int DestinationZone, int Item);
    // Keep the complete authored actions visible even when the planner cannot
    // execute their conditions yet. Missing support must not hide finale logic.
    public sealed record ScriptNode(int Zone,int Index,bool Disabled,
        IReadOnlyList<Condition> Conditions,IReadOnlyList<Instruction> Instructions);
    public sealed record TaskNode(int Zone, int RequiredItem, int RewardItem, bool Solved, int AdditionalRequiredItem, int AdditionalRewardItem)
    {
        public BotObjective? Instruction { get; set; }
    }
    public BotObjective? NextInstruction { get; set; }
    public List<Edge> Edges { get; } = new();
    public List<TaskNode> Tasks { get; } = new();
    public List<Dependency> Dependencies { get; } = new();
    public List<ScriptNode> Scripts { get; } = new();

    public static WorldActionGraph Build(GameData data, WorldMap world, GameState state, IReadOnlyDictionary<int, int> roomReturns,
        Func<Zone,int,int,bool>? canWalk=null,
        IReadOnlyDictionary<int,(int Zone,int X,int Y)>? doorReturnPoints=null)
    {
        var graph = new WorldActionGraph { _data = data, _state = state, _world=world, _canWalk=canWalk,
            _doorReturnPoints=doorReturnPoints ?? new Dictionary<int,(int Zone,int X,int Y)>() };
        bool Valid(int id) => id >= 0 && id < data.Zones.Count;
        foreach (var (zone, connections) in world.Connections)
        foreach (var (next, direction) in new[] { (connections.North, Direction.Up),
                     (connections.South, Direction.Down), (connections.East, Direction.Right),
                     (connections.West, Direction.Left) })
            if (next is int destination && Valid(destination))
                graph.Edges.Add(new(zone, destination, new BotObjective
                {
                    Type = ObjectiveType.ChangeZone, TargetZoneId = destination, Direction = direction,
                    Description = $"Walk {direction} from zone {zone} to {destination}",
                }));

        // Include only this world's outdoors, Dagobah, and authored rooms/transport links.
        var pending = new Queue<int>(world.Connections.Keys.Concat(world.DagobahZones)
            .Concat(world.Sectors.Keys).Distinct());
        var seen = new HashSet<int>();
        while (pending.TryDequeue(out int id))
        {
            if (!Valid(id) || !seen.Add(id)) continue;
            foreach(var (action,index) in data.Zones[id].Actions.Select((a,i)=>(a,i)))
            {
                graph.Scripts.Add(new(id,index,state.DisabledActions.Contains((id,index)),
                    action.Conditions.ToArray(),action.Instructions.ToArray()));
                // Discover script-only rooms regardless of their current guards.
                // Executable edges are still checked independently below.
                foreach(var change in action.Instructions.Where(i=>i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3))
                    if(Valid(change.Arguments[0])) pending.Enqueue(change.Arguments[0]);
            }
            foreach (var passage in ScriptedPassage.Instructions(data.Zones[id], state, world.Sectors.GetValueOrDefault(id),data.GameType))
                graph.Edges.Add(new(id, id, passage));
            foreach(var passage in ScriptedZoneTravel.Instructions(data.Zones[id],state,data,canWalk))
                if(passage.TargetZoneId is int destination && Valid(destination))
                {
                    pending.Enqueue(destination);
                    graph.Edges.Add(new(id,destination,passage));
                }
            foreach (var h in data.Zones[id].Objects)
            {
                bool door = h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit;
                bool vehicle = h.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary;
                bool wing = h.Type is ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah;
                if (!door && !vehicle && !wing && h.Type != ZoneObjectType.Teleporter) continue;
                int to = wing ? h.Type == ZoneObjectType.XWingFromDagobah ? world.LandingZoneId : world.XWingZoneId ?? world.StartingZoneId
                    : h.Argument;
                if (to == ushort.MaxValue && (roomReturns.TryGetValue(id, out int parent) ||
                    world.RoomParents.TryGetValue(id, out parent))) to = parent;
                if (!Valid(to) || to == id) continue;
                pending.Enqueue(to);
                // Discover linked zones even when the connection is not currently traversable.
                var boarding = vehicle ? ScriptedTransport.BoardingSpots(data.Zones[id],state,destination:to).ToList() : new();
                var itemBoarding=vehicle ? ScriptedTransport.ItemBoardingSpots(data.Zones[id],state,
                    world.Sectors.GetValueOrDefault(id),data.GameType,to).ToList() : new();
                // Landing scripts disable the transit hotspot while parked.
                // Its real boarding script can re-enable it before moving the
                // hero onto it; that authored route remains executable.
                bool scriptedEnable=vehicle && boarding.Count>0 && data.Zones[id].Actions.Any(a=>
                    a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=2 &&
                        boarding.Contains((c.Arguments[0],c.Arguments[1]))) &&
                    a.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableHotspot && i.Arguments.Count>0 &&
                        i.Arguments[0]==data.Zones[id].Objects.IndexOf(h)) &&
                    a.Instructions.LastOrDefault(i=>i.Opcode==InstructionOpcode.MoveHeroTo && i.Arguments.Count>=2) is { } landing &&
                        landing.Arguments[0]==h.X && landing.Arguments[1]==h.Y);
                if (!h.Enabled && !scriptedEnable) continue;
                if(vehicle && boarding.Count==0 && ScriptedTransport.BoardingSpots(data.Zones[id],state,false,to).Any()) continue;
                if(vehicle && boarding.Count==0 && itemBoarding.Count==0 &&
                    ScriptedTransport.ItemBoardingSpots(data.Zones[id],state,world.Sectors.GetValueOrDefault(id),
                        data.GameType,to,activeOnly:false).Any()) continue;
                if(vehicle && boarding.Count==0 && itemBoarding.Count==0)
                {
                    // Walk-on transports are unavailable while a native lock
                    // still covers their hotspot. A flavor bump cannot board it.
                    var nav=new Pathfinder(data,state) { AdditionalWalkability=canWalk,TransitionTarget=(id,h.X,h.Y) };
                    if(!nav.IsWalkable(data.Zones[id],h.X,h.Y)) continue;
                }
                var point = itemBoarding.Count>0 ? (itemBoarding[0].X,itemBoarding[0].Y) : boarding.Count>0 ? boarding[0] : (h.X,h.Y);
                graph.Edges.Add(new(id, to, new BotObjective
                {
                    Type = wing ? ObjectiveType.UseXWing : itemBoarding.Count>0 ? ObjectiveType.UseItemOnNpc :
                        boarding.Count>0 ? ObjectiveType.BumpTarget : ObjectiveType.EnterDoor,
                    RequiredItemId=itemBoarding.Count>0 ? itemBoarding[0].Item : null,
                    TargetZoneId = to, TargetX = point.Item1, TargetY = point.Item2,
                    Description = $"{(wing || vehicle ? "Board vehicle" : "Enter passage")} at ({point.Item1},{point.Item2}) to zone {to}",
                }));

            }
        }
        foreach (var sector in world.Sectors.Values.Distinct())
            graph.Tasks.Add(new(sector.Zone, sector.RequiredItem, sector.FindItem,
                sector.Solved || state.IsZoneSolved(sector.Zone), sector.AdditionalRequiredItem, sector.AdditionalGainItem));
        foreach (var task in graph.Tasks)
        foreach (int required in new[] { task.RequiredItem, task.AdditionalRequiredItem }.Where(i => i > 0).Distinct())
        foreach (var source in graph.Tasks.Where(s => s.Zone != task.Zone &&
                     (s.RewardItem == required || s.AdditionalRewardItem == required)))
            graph.Dependencies.Add(new(source.Zone, task.Zone, required));
        return graph;
    }

    public IReadOnlyList<Edge> Route(int from, int to, Func<Edge, bool> allowed,
        IReadOnlyCollection<(int X,int Y)>? targets = null)
        => RouteThroughAreas(from, to, allowed, targets);
}
