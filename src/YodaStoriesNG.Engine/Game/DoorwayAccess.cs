using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Repairs small static walls that seal an authored nested-room entrance.</summary>
public static class DoorwayAccess
{
    private readonly record struct Cell(int X,int Y);

    public static int OpenSealedRoomEntrances(GameData data,GameState state,WorldMap? world,Zone zone,
        IEnumerable<(int X,int Y)>? possibleEntries=null,bool writeLog=true)
    {
        if(world==null || zone.Width==0 || zone.Height==0) return 0;
        var starts=(possibleEntries ?? new[]{(state.PlayerX,state.PlayerY)})
            .Where(p=>Inside(zone,p.X,p.Y) && Walkable(data,state,zone,p.X,p.Y))
            .Select(p=>new Cell(p.X,p.Y)).Distinct().ToArray();
        if(starts.Length==0) return 0;
        int opened=0;
        foreach(var door in zone.Objects.Where(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance &&
            world.RoomParents.TryGetValue(h.Argument,out int parent) && parent==zone.Id).ToArray())
        {
            if(zone.Actions.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=2 &&
                c.Arguments[0]==door.X && c.Arguments[1]==door.Y))) continue;
            if(HasReachableApproach(data,state,zone,door.X,door.Y,starts)) continue;
            var pending=new PriorityQueue<Cell,(int Repairs,int Steps)>();
            var best=new Dictionary<Cell,(int Repairs,int Steps)>();
            var previous=new Dictionary<Cell,Cell>();
            foreach(var start in starts)
            {
                best[start]=(0,0);pending.Enqueue(start,(0,0));
            }
            Cell? goal=null;
            while(pending.TryDequeue(out var at,out var score))
            {
                if(!best.TryGetValue(at,out var current) || current!=score) continue;
                if(Math.Abs(at.X-door.X)+Math.Abs(at.Y-door.Y)==1 &&
                    (Walkable(data,state,zone,at.X,at.Y) || Repairable(data,state,zone,at.X,at.Y)))
                { goal=at;break; }
                foreach(var (dx,dy) in new[]{(0,-1),(1,0),(0,1),(-1,0)})
                {
                    int x=at.X+dx,y=at.Y+dy;
                    if(!Inside(zone,x,y) || IsReserved(zone,door,x,y)) continue;
                    int tile=zone.GetTile(x,y,1),repairs=score.Repairs;
                    if(!Walkable(data,state,zone,x,y))
                    {
                        if(!Repairable(data,state,zone,x,y) || repairs>=2) continue;
                        repairs++;
                    }
                    var next=new Cell(x,y);var candidate=(repairs,score.Steps+1);
                    if(best.TryGetValue(next,out var old) && old.CompareTo(candidate)<=0) continue;
                    best[next]=candidate;previous[next]=at;pending.Enqueue(next,candidate);
                }
            }
            if(goal is not { } destination || best[destination].Repairs is <1 or >2) continue;
            var origin=destination;
            while(previous.TryGetValue(origin,out var parent)) origin=parent;
            var path=new List<Cell>();
            for(var at=destination;at!=origin;at=previous[at]) path.Add(at);
            path.Reverse();
            var cleared=new List<Cell>();
            foreach(var cell in path)
                if(!Walkable(data,state,zone,cell.X,cell.Y))
                { zone.SetTile(cell.X,cell.Y,1,ushort.MaxValue);cleared.Add(cell); }
            if(cleared.Count>0)
            {
                opened+=cleared.Count;
                if(writeLog) Console.WriteLine($"[WORLD] Opened sealed nested-room doorway in zone {zone.Id} at ({door.X},{door.Y}); cleared {string.Join(' ',cleared.Select(c=>$"({c.X},{c.Y})"))}");
            }
        }
        return opened;
    }

    private static bool HasReachableApproach(GameData data,GameState state,Zone zone,int x,int y,Cell[] starts)
    {
        var seen=new HashSet<Cell>();var queue=new Queue<Cell>();
        foreach(var p in starts){var cell=new Cell(p.X,p.Y);if(seen.Add(cell))queue.Enqueue(cell);}
        while(queue.TryDequeue(out var at))
        {
            if(Math.Abs(at.X-x)+Math.Abs(at.Y-y)==1) return true;
            foreach(var d in new[]{(0,-1),(1,0),(0,1),(-1,0)})
            {
                var next=new Cell(at.X+d.Item1,at.Y+d.Item2);
                if(seen.Add(next) && Walkable(data,state,zone,next.X,next.Y)) queue.Enqueue(next);
            }
        }
        return false;
    }

    private static bool Walkable(GameData data,GameState state,Zone zone,int x,int y)
    {
        if(!Inside(zone,x,y) || TileCollision.IsGroundBlocked(data,zone,x,y) || ZoneArrival.HasLivingActor(zone,x,y,state)) return false;
        int tile=zone.GetTile(x,y,1);
        return tile>=data.Tiles.Count || !data.Tiles[tile].IsObject;
    }

    private static bool Repairable(GameData data,GameState state,Zone zone,int x,int y)
    {
        if(!Inside(zone,x,y) || ZoneArrival.HasLivingActor(zone,x,y,state) || IsReserved(zone,null,x,y)) return false;
        int tile=zone.GetTile(x,y,1);
        return tile<data.Tiles.Count && data.Tiles[tile].IsObject &&
            !data.Tiles[tile].IsDraggable && !data.Tiles[tile].IsCharacter;
    }

    private static bool IsReserved(Zone zone,ZoneObject? targetDoor,int x,int y) =>
        zone.Objects.Any(h=>h.X==x && h.Y==y && (!ReferenceEquals(h,targetDoor) || h.Type!=ZoneObjectType.DoorEntrance));

    private static bool Inside(Zone zone,int x,int y)=>x>=0 && y>=0 && x<zone.Width && y<zone.Height;
}
