using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Directed, same-area passages authored as standing/bump triggers and hero relocation.</summary>
public static class ScriptedPassage
{
    // Yoda's animated relocation names an absolute destination, while Indy's
    // equivalent is an offset. Follow the complete sequence in instruction order.
    internal static (int X,int Y)? Arrival(Data.Action action,GameType gameType)
    {
        (int X,int Y)? position=null;
        foreach(var instruction in action.Instructions)
        {
            var a=instruction.Arguments;
            if(instruction.Opcode==InstructionOpcode.MoveHeroTo && a.Count>=2)
                position=(a[0],a[1]);
            else if(instruction.Opcode==InstructionOpcode.MoveHeroBy)
            {
                if(gameType==GameType.YodaStories && a.Count>=4) position=(a[2],a[3]);
                else if(a.Count>=2 && position is { } p) position=(p.X+a[0],p.Y+a[1]);
            }
        }
        return position;
    }

    internal static Data.Action? ArrivalAction(Zone zone,Data.Action action,GameState state,GameType gameType,Sector? sector=null)
    {
        var seen=new HashSet<Data.Action>();
        Data.Action? lastMovement=null;
        while(seen.Add(action))
        {
            if(action.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone)) return null;
            if(Arrival(action,gameType)!=null) lastMovement=action;
            var signal=action.Instructions.LastOrDefault(i=>i.Arguments.Count>0 &&
                i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.SetRandom);
            if(signal==null) return lastMovement;
            var guard=signal.Opcode==InstructionOpcode.SetCounter ? ConditionOpcode.CounterIs : ConditionOpcode.RandomIs;
            var consumers=zone.Actions.Where((a,index)=>!state.DisabledActions.Contains((zone.Id,index)) &&
                a.Conditions.Any(c=>c.Opcode==guard && c.Arguments.Count>0 && c.Arguments[0]==signal.Arguments[0]) &&
                a.Conditions.All(c=>c.Arguments.Count>0 && (c.Opcode==guard && c.Arguments[0]==signal.Arguments[0] ||
                    c.Opcode==ConditionOpcode.RequiredItemIs && sector!=null &&
                        (sector.RequiredItem==c.Arguments[0] || sector.AdditionalRequiredItem==c.Arguments[0]) ||
                    c.Opcode==ConditionOpcode.FindItemIs && sector?.FindItem==c.Arguments[0]))).ToArray();
            // A signal may also repaint the room. The relocation consumer is the
            // one that determines arrival; matching cosmetic consumers can coexist.
            var relocating=consumers.Where(a=>Arrival(a,gameType)!=null &&
                !a.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone)).ToArray();
            if(relocating.Length==1) { action=relocating[0]; continue; }
            if(consumers.Length!=1) return lastMovement;
            action=consumers[0];
        }
        return lastMovement;
    }

    public static bool IsTransportAnimation(Zone zone,Data.Action action,GameType gameType)
    {
        var arrival=Arrival(action,gameType);
        return arrival!=null && action.Instructions.Any(i=>i.Opcode==InstructionOpcode.HideHero) &&
            zone.Objects.Any(h=>h.X==arrival.Value.X && h.Y==arrival.Value.Y && h.Type is
                ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary or
                ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah);
    }

    public static IEnumerable<BotObjective> Instructions(Zone zone, GameState state, Sector? sector = null, GameType gameType = GameType.YodaStories)
    {
        foreach (var (action, index) in zone.Actions.Select((a, i) => (a, i)))
        {
            if (state.DisabledActions.Contains((zone.Id, index)) ||
                action.Instructions.Any(i => i.Opcode == InstructionOpcode.ChangeZone) ||
                IsTransportAnimation(zone,action,gameType)) continue;
            var trigger = action.Conditions.FirstOrDefault(c => c.Opcode is ConditionOpcode.StandingOn or ConditionOpcode.Bump or
                ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 3);
            var movement=ArrivalAction(zone,action,state,gameType,sector);
            if(movement==null || IsTransportAnimation(zone,movement,gameType)) continue;
            var arrival = Arrival(movement,gameType);
            if (trigger == null || arrival == null) continue;
            int x = trigger.Arguments[0], y = trigger.Arguments[1];
            int ax = arrival.Value.X, ay = arrival.Value.Y;
            bool placed = trigger.Opcode == ConditionOpcode.PlacedItemIs;
            if(placed && (trigger.Arguments.Count<5 || trigger.Arguments[2] is <0 or >2)) continue;
            int? item = placed ? trigger.Arguments[4] == -1 ? sector?.RequiredItem : trigger.Arguments[4] : null;
            if(placed && (item is not >0 || !state.HasItem(item.Value))) continue;
            if (x < 0 || y < 0 || x >= zone.Width || y >= zone.Height ||
                ax < 0 || ay < 0 || ax >= zone.Width || ay >= zone.Height ||
                (x == ax && y == ay) || !(placed && trigger.Arguments[3]==-1) && zone.GetTile(x, y, placed ? trigger.Arguments[2] :
                    trigger.Opcode==ConditionOpcode.Bump ? 1 : 0) != unchecked((ushort)trigger.Arguments[placed ? 3 : 2])) continue;
            // Only known, satisfied guards produce a traversable edge. An entry cutscene
            // or an unmodelled condition must not become an invented passage.
            if (!action.Conditions.All(c => c.Arguments.Count > 0 && (c.Opcode switch
            {
                ConditionOpcode.StandingOn => c == trigger,
                ConditionOpcode.Bump => c == trigger,
                ConditionOpcode.PlacedItemIs => c == trigger,
                ConditionOpcode.RequiredItemIs => sector!=null && (sector.RequiredItem==c.Arguments[0] || sector.AdditionalRequiredItem==c.Arguments[0]),
                ConditionOpcode.FindItemIs => sector?.FindItem==c.Arguments[0],
                ConditionOpcode.CounterIs => state.GetCounter(zone.Id) == c.Arguments[0],
                ConditionOpcode.CounterIsNot => state.GetCounter(zone.Id) != c.Arguments[0],
                ConditionOpcode.RandomIs => state.ZoneRandom.GetValueOrDefault(zone.Id) == c.Arguments[0],
                ConditionOpcode.RandomIsGreaterThan => state.ZoneRandom.GetValueOrDefault(zone.Id) > c.Arguments[0],
                ConditionOpcode.RandomIsLessThan => state.ZoneRandom.GetValueOrDefault(zone.Id) < c.Arguments[0],
                ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable when c.Arguments.Count>=4 =>
                    zone.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])==unchecked((ushort)c.Arguments[0]),
                _ => false,
            }))) continue;
            yield return new BotObjective
            {
                Type = ObjectiveType.TraversePassage, TargetZoneId = zone.Id,
                TargetX = x, TargetY = y, ArrivalX = ax, ArrivalY = ay,
                PassageRequiresBump = trigger.Opcode==ConditionOpcode.Bump || placed,
                RequiredItemId = item,
                ScriptIndex = index,
                AvoidFloorSwitches = true,
                Description = $"Follow scripted passage ({x},{y}) to ({ax},{ay}) in zone {zone.Id}",
            };
        }
    }
}
