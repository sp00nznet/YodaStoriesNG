using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Resolve authored boarding bumps separately from off-screen transit markers.</summary>
public static class ScriptedTransport
{
    public static IEnumerable<(int X,int Y,int Item,int Action)> ItemBoardingSpots(Zone zone,GameState state,
        Sector? sector,GameType gameType,int destination,bool activeOnly=true)
    {
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
        {
            var placed=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5);
            if(placed==null || !StartsFlight(zone,action,state)) continue;
            var arrival=ScriptedPassage.ArrivalAction(zone,action,state,gameType,sector);
            var landing=arrival==null ? null : ScriptedPassage.Arrival(arrival,gameType);
            bool transfer=action.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone &&
                i.Arguments.Count>=3 && i.Arguments[0]==destination);
            if(!transfer && (landing==null || !zone.Objects.Any(h=>h.Argument==destination &&
                h.X==landing.Value.X && h.Y==landing.Value.Y &&
                h.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary))) continue;
            int item=placed.Arguments[4]<0 ? sector?.RequiredItem ?? -1 : placed.Arguments[4];
            if(item<0) continue;
            if(activeOnly && (state.DisabledActions.Contains((zone.Id,index)) || !state.HasItem(item) ||
                !action.Conditions.All(c=>c.Opcode switch {
                    ConditionOpcode.PlacedItemIs=>c.Arguments.Count>=5 && c.Arguments[2] is >=0 and <=2 &&
                        (c.Arguments[4]<0 ? sector?.RequiredItem ?? -1 : c.Arguments[4])==item &&
                        zone.GetTile(c.Arguments[0],c.Arguments[1],c.Arguments[2])==unchecked((ushort)c.Arguments[3]),
                    ConditionOpcode.HasItem=>c.Arguments.Count>0 && state.HasItem(c.Arguments[0]<0 ? sector?.RequiredItem ?? -1 : c.Arguments[0]),
                    ConditionOpcode.RequiredItemIs=>c.Arguments.Count>0 && sector?.RequiredItem==c.Arguments[0],
                    ConditionOpcode.CounterIs=>c.Arguments.Count>0 && state.GetCounter(zone.Id)==c.Arguments[0],
                    ConditionOpcode.CounterIsNot=>c.Arguments.Count>0 && state.GetCounter(zone.Id)!=c.Arguments[0],
                    ConditionOpcode.RandomIs=>c.Arguments.Count>0 && state.ZoneRandom.GetValueOrDefault(zone.Id)==c.Arguments[0],
                    ConditionOpcode.TileAtIs when c.Arguments.Count>=4=>
                        zone.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])==unchecked((ushort)c.Arguments[0]),
                    _=>false }))) continue;
            yield return(placed.Arguments[0],placed.Arguments[1],item,index);
        }
    }

    public static (int X,int Y)? Arrival(Zone zone,GameState state,GameType gameType,Sector? sector=null)
    {
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)).Reverse())
        {
            if(state.DisabledActions.Contains((zone.Id,index)) ||
                !action.Conditions.Any(c=>c.Opcode==ConditionOpcode.EnterByPlane)) continue;
            var movement=ScriptedPassage.ArrivalAction(zone,action,state,gameType);
            if(movement==null || ScriptedPassage.Arrival(movement,gameType) is not { } arrival) continue;
            if(!action.Conditions.All(c=>c.Opcode switch {
                // EnterByPlane is a zero-argument arrival trigger. Do not apply
                // the argument checks used by counter/tile conditions to it.
                ConditionOpcode.EnterByPlane=>true,
                ConditionOpcode.HeroIsAt=>c.Arguments.Count>=2 &&
                    c.Arguments[0]==arrival.X && c.Arguments[1]==arrival.Y,
                ConditionOpcode.CounterIs when c.Arguments.Count>0=>state.GetCounter(zone.Id)==c.Arguments[0],
                ConditionOpcode.CounterIsNot when c.Arguments.Count>0=>state.GetCounter(zone.Id)!=c.Arguments[0],
                ConditionOpcode.SectorCounterIs when c.Arguments.Count>0=>state.GetSectorCounter(zone.Id)==c.Arguments[0],
                ConditionOpcode.SectorCounterIsNot when c.Arguments.Count>0=>state.GetSectorCounter(zone.Id)!=c.Arguments[0],
                ConditionOpcode.HasItem when c.Arguments.Count>0=>state.HasItem(c.Arguments[0]<0 ? sector?.RequiredItem ?? -1 : c.Arguments[0]),
                ConditionOpcode.RequiredItemIs when c.Arguments.Count>0=>sector?.RequiredItem==c.Arguments[0],
                ConditionOpcode.RandomIs when c.Arguments.Count>0=>state.ZoneRandom.GetValueOrDefault(zone.Id)==c.Arguments[0],
                ConditionOpcode.RandomIsNot when c.Arguments.Count>0=>state.ZoneRandom.GetValueOrDefault(zone.Id)!=c.Arguments[0],
                ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable when c.Arguments.Count>=4=>
                    zone.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])==unchecked((ushort)c.Arguments[0]),
                _=>false })) continue;
            return arrival;
        }
        return null;
    }

    public static IEnumerable<(int X,int Y)> BoardingSpots(Zone zone,GameState state,bool activeOnly=true,int? destination=null)
    {
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
        {
            var bump=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3);
            if(bump==null) continue;
            var transfers=action.Instructions.Where(i=>i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3).ToList();
            if(destination is int target && transfers.Count>0 && transfers.All(i=>i.Arguments[0]!=target)) continue;
            bool startsFlight=StartsFlight(zone,action,state);
            if(!startsFlight) continue;
            if(activeOnly)
            {
                if(state.DisabledActions.Contains((zone.Id,index))) continue;
                if(zone.GetTile(bump.Arguments[0],bump.Arguments[1],1)!=unchecked((ushort)bump.Arguments[2])) continue;
                if(action.Conditions.Any(c=>c.Arguments.Count>0 && (c.Opcode switch
                {
                    ConditionOpcode.CounterIs => state.GetCounter(zone.Id)!=c.Arguments[0],
                    ConditionOpcode.CounterIsNot => state.GetCounter(zone.Id)==c.Arguments[0],
                    ConditionOpcode.RandomIs => state.ZoneRandom.GetValueOrDefault(zone.Id)!=c.Arguments[0],
                    _=>false,
                }))) continue;
            }
            yield return(bump.Arguments[0],bump.Arguments[1]);
        }
    }
    internal static bool StartsFlight(Zone zone,Data.Action start,GameState state)
    {
        var pending=new Queue<Data.Action>();
        var visited=new HashSet<Data.Action>();
        bool hidesHero=false, changesZone=false;
        pending.Enqueue(start);
        while(pending.TryDequeue(out var action))
        {
            if(!visited.Add(action)) continue;
            hidesHero |= action.Instructions.Any(i=>i.Opcode==InstructionOpcode.HideHero);
            changesZone |= action.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone);
            foreach(var signal in action.Instructions.Where(i=>i.Arguments.Count>0 &&
                i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.SetRandom or InstructionOpcode.SetSectorCounter))
            {
                var guard=signal.Opcode==InstructionOpcode.SetCounter ? ConditionOpcode.CounterIs :
                    signal.Opcode==InstructionOpcode.SetSectorCounter ? ConditionOpcode.SectorCounterIs : ConditionOpcode.RandomIs;
                foreach(var (next,index) in zone.Actions.Select((a,i)=>(a,i)))
                    if(!state.DisabledActions.Contains((zone.Id,index)) &&
                        next.Conditions.Any(c=>c.Opcode==guard && c.Arguments.Count>0 && c.Arguments[0]==signal.Arguments[0]))
                        pending.Enqueue(next);
            }
        }
        // Conversations and final-room cutscenes hide Luke too. Only treat
        // that animation as boarding when it has an actual travel destination.
        return hidesHero && (changesZone || zone.Objects.Any(h=>h.Type is
            ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary));
    }

}
