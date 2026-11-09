using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Authored doors whose actual trigger/arrival differ from hotspot metadata.</summary>
public static class ScriptedZoneTravel
{
    // Structural links retain their trigger even when its tile is not available
    // yet. Used only for prerequisite planning, never as executable travel edges.
    public static IEnumerable<(int Action, Instruction Change,bool ByPlane)> ConditionalLinks(Zone zone,GameState state)
    {
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
        {
            if(state.DisabledActions.Contains((zone.Id,index)) || !action.Conditions.Any(c=>
                c.Opcode is ConditionOpcode.Bump or ConditionOpcode.StandingOn or ConditionOpcode.PlacedItemIs)) continue;
            var seen=new HashSet<Data.Action>();
            var current=action;
            bool hidden=zone.Id==state.CurrentZoneId && !state.HeroVisible;
            while(seen.Add(current))
            {
                foreach(var instruction in current.Instructions)
                    if(instruction.Opcode==InstructionOpcode.HideHero) hidden=true;
                    else if(instruction.Opcode==InstructionOpcode.ShowHero) hidden=false;
                var change=current.Instructions.LastOrDefault(i=>i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3);
                if(change!=null) { yield return (index,change,hidden); break; }
                var signal=current.Instructions.LastOrDefault(i=>i.Arguments.Count>0 &&
                    i.Opcode is InstructionOpcode.SetRandom or InstructionOpcode.SetCounter or InstructionOpcode.SetSectorCounter);
                if(signal==null) break;
                var guard=signal.Opcode==InstructionOpcode.SetRandom ? ConditionOpcode.RandomIs :
                    signal.Opcode==InstructionOpcode.SetSectorCounter ? ConditionOpcode.SectorCounterIs : ConditionOpcode.CounterIs;
                var consumers=zone.Actions.Where((a,i)=>!state.DisabledActions.Contains((zone.Id,i)) &&
                    a.Conditions.Count==1 && a.Conditions[0].Opcode==guard &&
                    a.Conditions[0].Arguments.Count>0 && a.Conditions[0].Arguments[0]==signal.Arguments[0]).ToList();
                // Cosmetic consumers can share a phase with its departure.
                // Follow only an unambiguous control-changing consumer.
                var controls=consumers.Where(a=>a.Instructions.Any(i=>i.Opcode is InstructionOpcode.ChangeZone or
                    InstructionOpcode.SetCounter or InstructionOpcode.SetRandom or InstructionOpcode.SetSectorCounter)).ToList();
                if(controls.Count!=1) break;
                current=controls[0];
            }
        }
    }

    public static IEnumerable<BotObjective> Instructions(Zone zone,GameState state,GameData data,Func<Zone,int,int,bool>? canWalk=null)
    {
        var links=ConditionalLinks(zone,state).ToDictionary(l=>l.Action);
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
        {
            if(state.DisabledActions.Contains((zone.Id,index))) continue;
            var trigger=action.Conditions.FirstOrDefault(c=>c.Arguments.Count>=3 &&
                c.Opcode is ConditionOpcode.Bump or ConditionOpcode.StandingOn);
            if(!links.TryGetValue(index,out var link)) continue;
            var change=link.Change;
            if(trigger==null || change==null || change.Arguments[0]<0 || change.Arguments[0]>=data.Zones.Count) continue;
            var t=trigger.Arguments;
            if(zone.GetTile(t[0],t[1],trigger.Opcode==ConditionOpcode.Bump ? 1 : 0)!=unchecked((ushort)t[2])) continue;
            var stance=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.HeroIsAt && c.Arguments.Count>=2);
            if(stance!=null && (trigger.Opcode!=ConditionOpcode.Bump ||
                Math.Max(Math.Abs(stance.Arguments[0]-t[0]),Math.Abs(stance.Arguments[1]-t[1]))!=1)) continue;
            bool valid=action.Conditions.All(c=>c.Arguments.Count>0 && (c.Opcode switch {
                ConditionOpcode.Bump or ConditionOpcode.StandingOn => c==trigger,
                ConditionOpcode.HeroIsAt => c==stance,
                ConditionOpcode.CounterIs => state.GetCounter(zone.Id)==c.Arguments[0],
                ConditionOpcode.CounterIsNot => state.GetCounter(zone.Id)!=c.Arguments[0],
                ConditionOpcode.SectorCounterIs => state.GetSectorCounter(zone.Id)==c.Arguments[0],
                ConditionOpcode.SectorCounterIsNot => state.GetSectorCounter(zone.Id)!=c.Arguments[0],
                ConditionOpcode.RandomIs => state.ZoneRandom.GetValueOrDefault(zone.Id)==c.Arguments[0],
                ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable when c.Arguments.Count>=4 =>
                    zone.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])==unchecked((ushort)c.Arguments[0]),
                _ => false
            }));
            if(!valid) continue;
            var final=ResolveArrival(data,state,change.Arguments[0],change.Arguments[1],change.Arguments[2],link.ByPlane,canWalk);
            if(final==null) continue;
            var arrival=final.Value;
            yield return new BotObjective { Type=ObjectiveType.TraversePassage,
                TargetX=t[0],TargetY=t[1],TargetZoneId=arrival.Zone,
                ArrivalX=arrival.X,ArrivalY=arrival.Y,
                ApproachX=stance?.Arguments[0],ApproachY=stance?.Arguments[1],
                PassageRequiresBump=trigger.Opcode==ConditionOpcode.Bump,ScriptedZoneChange=true,
                Description=$"Use scripted entrance ({t[0]},{t[1]}) in zone {zone.Id} to zone {arrival.Zone}" };
        }
    }

    // Transit animation rooms can redirect an arrival before control returns.
    // Resolve known scalar guards in native order without mutating the world.
    // Unknown guarded transfers are left unproved rather than inventing a link.
    private static (int Zone,int X,int Y)? ResolveArrival(GameData data,GameState state,int id,int x,int y,bool plane,
        Func<Zone,int,int,bool>? canWalk)
    {
        var counters=new Dictionary<int,int>();var random=new Dictionary<int,int>();var sectors=new Dictionary<int,int>();
        var seen=new HashSet<(int,int,bool,int)>();
        for(int hops=0;hops<16;hops++)
        {
            if(id<0 || id>=data.Zones.Count || !seen.Add((id,counters.GetValueOrDefault(id,state.GetCounter(id)),plane,
                sectors.GetValueOrDefault(id,state.GetSectorCounter(id))))) return null;
            var area=data.Zones[id];
            var pos=ZoneArrival.ResolveSpawn(data,area,x,y,plane,canWalk);x=pos.X;y=pos.Y;
            bool enteredByPlane=plane;
            int counter=counters.GetValueOrDefault(id,state.GetCounter(id));
            int roll=random.GetValueOrDefault(id,state.ZoneRandom.GetValueOrDefault(id));
            int sector=sectors.GetValueOrDefault(id,state.GetSectorCounter(id));
            Instruction? redirect=null;
            foreach(var (action,index) in area.Actions.Select((a,i)=>(a,i)))
            {
                if(state.DisabledActions.Contains((id,index)) || !action.Conditions.Any(c=>c.Opcode==ConditionOpcode.EnterByPlane)) continue;
                bool? Matches(Condition c)=>c.Opcode switch {
                    ConditionOpcode.EnterByPlane=>enteredByPlane,
                    ConditionOpcode.CounterIs when c.Arguments.Count>0=>counter==c.Arguments[0],
                    ConditionOpcode.CounterIsNot when c.Arguments.Count>0=>counter!=c.Arguments[0],
                    ConditionOpcode.RandomIs when c.Arguments.Count>0=>roll==c.Arguments[0],
                    ConditionOpcode.RandomIsNot when c.Arguments.Count>0=>roll!=c.Arguments[0],
                    ConditionOpcode.SectorCounterIs when c.Arguments.Count>0=>sector==c.Arguments[0],
                    ConditionOpcode.SectorCounterIsNot when c.Arguments.Count>0=>sector!=c.Arguments[0],
                    ConditionOpcode.HasItem when c.Arguments.Count>0 && c.Arguments[0]>=0=>state.HasItem(c.Arguments[0]),
                    ConditionOpcode.HeroIsAt when c.Arguments.Count>=2=>x==c.Arguments[0] && y==c.Arguments[1],
                    ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable when c.Arguments.Count>=4=>
                        area.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])==unchecked((ushort)c.Arguments[0]),
                    _=>null };
                var guards=action.Conditions.Select(Matches).ToArray();
                if(guards.Contains(false)) continue;
                if(guards.Any(g=>g==null))
                {if(action.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone)) return null;continue;}
                foreach(var instruction in action.Instructions)
                {
                    var a=instruction.Arguments;
                    if(instruction.Opcode==InstructionOpcode.ChangeZone && a.Count>=3) {redirect=instruction;break;}
                    if(a.Count>0) switch(instruction.Opcode) {
                        case InstructionOpcode.SetCounter: counter=a[0];break;
                        case InstructionOpcode.SetRandom: roll=a[0];break;
                        case InstructionOpcode.SetSectorCounter: sector=a[0];break;
                        case InstructionOpcode.MoveHeroTo when a.Count>=2: x=a[0];y=a[1];break;
                        case InstructionOpcode.MoveHeroBy when data.GameType==GameType.YodaStories && a.Count>=4:
                            x=a[2];y=a[3];break;
                        case InstructionOpcode.MoveHeroBy when data.GameType==GameType.IndianaJones && a.Count>=2:
                            x+=a[0];y+=a[1];break;
                    }
                    if(instruction.Opcode==InstructionOpcode.HideHero) plane=true;
                    if(instruction.Opcode==InstructionOpcode.ShowHero) plane=false;
                }
                if(redirect!=null) break;
            }
            if(redirect==null) return(id,x,y);
            counters[id]=counter;random[id]=roll;sectors[id]=sector;
            id=redirect.Arguments[0];x=redirect.Arguments[1];y=redirect.Arguments[2];
        }
        return null;
    }
}
