using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Bounded search of push/pull states without modifying the live map.</summary>
public static class BlockPuzzlePlanner
{
    public sealed record Step(int X, int Y, Direction Direction, bool Pull, string Before, string After, bool Walk = false);
    public sealed record Placement(int X, int Y, int Tile);
    private sealed record Node(int Hero, int[] Blocks, int Parent, Step? Step, HashSet<int> Reach, int Cost = 0);
    public static string Layout(Zone zone) => Signature(zone.TileGrid);
    private static string Signature(ushort[,,] grid)
    {
        var bytes = new byte[grid.Length * sizeof(ushort)];
        Buffer.BlockCopy(grid, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static IReadOnlyList<Step>? Plan(GameData data, Zone zone, GameState state,
        IReadOnlyCollection<(int X, int Y)> targets, IReadOnlyCollection<Placement> placements,
        int maxStates = 12000, (int X, int Y)? transitionTarget = null,
        IReadOnlyCollection<(int X, int Y)>? clearTiles = null)
        => PlanCore(data,zone,state,targets,placements,maxStates,transitionTarget,clearTiles,false) ??
            (targets.Count>0 && placements.Count==0 && (clearTiles?.Count ?? 0)==0
                ? PlanCore(data,zone,state,targets,placements,maxStates,transitionTarget,clearTiles,true) : null);

    private static IReadOnlyList<Step>? PlanCore(GameData data,Zone zone,GameState state,
        IReadOnlyCollection<(int X,int Y)> targets,IReadOnlyCollection<Placement> placements,
        int maxStates,(int X,int Y)? transitionTarget,IReadOnlyCollection<(int X,int Y)>? clearTiles,
        bool prioritizeNearby)
    {
        bool trace=Environment.GetEnvironmentVariable("YSNG_BLOCK_TRACE")=="1";
        int w = zone.Width, h = zone.Height;
        clearTiles ??= Array.Empty<(int X, int Y)>();
        int Cell(int x, int y) => y * w + x;
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
        var positions = new List<int>(); var tiles = new List<ushort>();
        var floor = new ushort[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            ushort t = zone.GetTile(x, y, 1); floor[Cell(x,y)] = t;
            if (t < data.Tiles.Count && data.Tiles[t].IsDraggable)
            { positions.Add(Cell(x,y)); tiles.Add(t); floor[Cell(x,y)] = ushort.MaxValue; }
        }
        if (positions.Count == 0 || targets.Count == 0 && placements.Count == 0) return null;
        // Native enemy pictures can remain in the tile grid while their actor
        // occupies the corridor. They are clearable by combat, not permanent
        // terrain; the executor attacks before entering an occupied route cell.
        bool EnemyMarker(int x,int y)=>state.ZoneNPCs.Any(n=>n.IsHostile &&
            (n.X==x && n.Y==y || n.StartX==x && n.StartY==y));
        var nav = new Pathfinder(data,state) { AllowDiagonal=true, PreferCardinal=true,
            AdditionalWalkability = (area, x, y) => area.GetTile(x,y,1) is var tile &&
                (tile >= data.Tiles.Count || !data.Tiles[tile].IsCharacter || EnemyMarker(x,y)) };
        if (transitionTarget is { } transit) nav.TransitionTarget = (zone.Id,transit.X,transit.Y);
        // Moving actors are temporary obstacles. The executor clears hostiles
        // and waits for wandering friendlies; stationary friendlies remain walls.
        var planningActors = state.ZoneNPCs.Where(n => !n.IsHostile &&
            n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList();
        var actors = planningActors.Where(n => n.IsEnabled && n.IsAlive).Select(n => Cell(n.X,n.Y)).ToHashSet();
        var dirs = new[] { (Direction.Up,0,-1), (Direction.Down,0,1), (Direction.Left,-1,0), (Direction.Right,1,0) };
        var walkingArea=new Zone { Id=zone.Id,Width=zone.Width,Height=zone.Height,
            TileGrid=Grid(Array.Empty<int>()),Objects=zone.Objects,Actions=zone.Actions };
        var standing=new bool[w*h]; var landingCells=new bool[w*h];
        for(int cell=0;cell<w*h;cell++)
        {
            int t=floor[cell];
            bool allowed=!actors.Contains(cell) && !nav.IsUnrelatedTransition(zone,cell%w,cell/w);
            landingCells[cell]=allowed && t==ushort.MaxValue;
            // Compile the same standing rules as live walking. Crates need
            // an empty destination even where a loose pickup is walkable.
            standing[cell]=allowed && nav.IsWalkable(walkingArea,cell%w,cell/w,planningActors,zone.Id);
        }
        bool Open(int cell, HashSet<int> blocks, bool landing = false) =>
            cell>=0 && cell<w*h && !blocks.Contains(cell) && (landing ? landingCells[cell] : standing[cell]);
        // Walking rules depend on permanent terrain; crates only remove their
        // occupied destinations. Compile those edges once rather than rebuilding
        // grids and evaluating every script for every flood fill in the search.
        var walkingEdges=Enumerable.Range(0,w*h).Select(cell=>nav.WalkingNeighbors(walkingArea,
            cell%w,cell/w,planningActors,zone.Id).Select(next=>Cell(next.X,next.Y)).ToArray()).ToArray();
        HashSet<int> Reach(int hero, int[] blocks)
        {
            var occupied=blocks.ToHashSet();
            var seen=new HashSet<int>{hero}; var q=new Queue<int>(); q.Enqueue(hero);
            while(q.TryDequeue(out int at)) foreach(int next in walkingEdges[at])
                if(!occupied.Contains(next) && seen.Add(next)) q.Enqueue(next);
            return seen;
        }
        string Key(int[] blocks, HashSet<int> reach) => reach.Min()+"|"+string.Join(';',
            blocks.Select((p,i)=>(Tile:tiles[i],Pos:p)).OrderBy(p=>p.Tile).ThenBy(p=>p.Pos).Select(p=>$"{p.Tile}:{p.Pos}"));
        ushort[,,] Grid(int[] blocks)
        {
            var grid = (ushort[,,])zone.TileGrid.Clone();
            foreach (int at in positions) grid[at/w,at%w,1] = ushort.MaxValue;
            for (int i=0;i<blocks.Length;i++) grid[blocks[i]/w,blocks[i]%w,1]=tiles[i];
            return grid;
        }
        string Map(int[] blocks) => Signature(Grid(blocks));
        // Distance through the permanent terrain guides preparation through
        // winding rooms. Manhattan distance cannot distinguish opening the real
        // corridor from shuffling unrelated rocks on the other side of a wall.
        var optimisticArea = new Zone { Id=zone.Id,Width=zone.Width,Height=zone.Height,
            TileGrid=Grid(Array.Empty<int>()),Objects=zone.Objects,Actions=zone.Actions };
        var predecessors = Enumerable.Range(0,w*h).Select(_=>new List<int>()).ToArray();
        for(int y=0;y<h;y++) for(int x=0;x<w;x++)
            if(nav.IsWalkable(optimisticArea,x,y,planningActors,zone.Id))
                foreach(var next in nav.WalkingNeighbors(optimisticArea,x,y,planningActors,zone.Id))
                    predecessors[Cell(next.X,next.Y)].Add(Cell(x,y));
        var goalDistance=Enumerable.Repeat(w*h+1,w*h).ToArray();
        var distanceQueue=new Queue<int>();
        var heuristicTargets=clearTiles.Count>0 ? clearTiles : targets;
        foreach(var target in heuristicTargets.Where(t=>In(t.X,t.Y)))
        {
            int cell=Cell(target.X,target.Y);
            goalDistance[cell]=0;
            distanceQueue.Enqueue(cell);
        }
        while(distanceQueue.TryDequeue(out int at))
            foreach(int previous in predecessors[at])
                if(goalDistance[previous]>goalDistance[at]+1)
                { goalDistance[previous]=goalDistance[at]+1; distanceQueue.Enqueue(previous); }
        // Guide each crate through permanent terrain, rather than measuring
        // straight through walls. Other movable crates remain searchable.
        var placementDistances=new Dictionary<Placement,int[]>();
        var noBlocks=new HashSet<int>();
        foreach(var placement in placements)
        {
            var distances=Enumerable.Repeat(w*h+1,w*h).ToArray();
            var pending=new Queue<int>();
            if(In(placement.X,placement.Y))
            { int at=Cell(placement.X,placement.Y); distances[at]=0; pending.Enqueue(at); }
            while(pending.TryDequeue(out int at))
            foreach(var (_,dx,dy) in dirs)
            {
                int x=at%w-dx,y=at/w-dy;
                if(!In(x,y) || !Open(Cell(x,y),noBlocks,true) || !Open(at,noBlocks,true)) continue;
                bool push=In(x-dx,y-dy) && Open(Cell(x-dx,y-dy),noBlocks);
                bool pull=In(at%w+dx,at/w+dy) && Open(Cell(at%w+dx,at/w+dy),noBlocks);
                int previous=Cell(x,y);
                if((push || pull) && distances[previous]>distances[at]+1)
                { distances[previous]=distances[at]+1; pending.Enqueue(previous); }
            }
            placementDistances[placement]=distances;
            if(!positions.Select((at,i)=>(at,i)).Any(b=>tiles[b.i]==placement.Tile && distances[b.at]<=w*h)) return null;
        }
        double Priority(Node node,HashSet<int> reach)
        {
            int distance=heuristicTargets.Count==0 ? 0 : reach.Min(at=>goalDistance[at]);
            // Geometric distance alone favors shuffling dozens of unrelated
            // crates in dense rooms. Count occupied cells on the cheapest
            // remaining walking route as well. This guides the bounded search;
            // it does not discard moves or claim a solution without replay.
            int barriers=0;
            if(targets.Count>0 && placements.Count==0)
            {
                var occupied=node.Blocks.ToHashSet();
                var costs=Enumerable.Repeat(w*h,w*h).ToArray();
                var pending=new PriorityQueue<int,int>();
                foreach(var target in targets.Where(t=>In(t.X,t.Y)))
                {int at=Cell(target.X,target.Y);costs[at]=0;pending.Enqueue(at,0);}
                while(pending.TryDequeue(out int at,out int cost))
                {
                    if(cost!=costs[at]) continue;
                    if(reach.Contains(at)) {barriers=cost;break;}
                    int next=cost+(occupied.Contains(at) ? 1 : 0);
                    foreach(int before in predecessors[at])
                        if(next<costs[before]) {costs[before]=next;pending.Enqueue(before,next);}
                }
            }
            int misplaced=placements.Count(p=>!node.Blocks.Select((at,i)=>(at,i))
                .Any(b=>b.at==Cell(p.X,p.Y)&&tiles[b.i]==p.Tile));
            int placementDistance=placements.Sum(p=>node.Blocks.Select((at,i)=>(at,i))
                .Where(b=>tiles[b.i]==p.Tile).Select(b=>placementDistances[p][b.at])
                .DefaultIfEmpty(w*h).Min());
            // Bounded best-first search favors moves that open an approach to the
            // actual objective, rather than enumerating unrelated rocks first.
            int covered=clearTiles.Count(t=>node.Blocks.Contains(Cell(t.X,t.Y)));
            double accessGain=placements.Count==0 && clearTiles.Count==0 ? 2 : 0.001;
            return node.Cost+16*barriers+4*distance+4*placementDistance+8*(misplaced+covered)-reach.Count*accessGain;
        }
        var initialBlocks=positions.ToArray();
        var coveringBlocks=positions.Select((cell,index)=>(cell,index))
            .Where(p=>clearTiles.Any(t=>Cell(t.X,t.Y)==p.cell)).Select(p=>p.index).ToHashSet();
        int initialHero=Cell(state.PlayerX,state.PlayerY);
        var initialReach=Reach(initialHero,initialBlocks);
        if(trace) Console.WriteLine($"[BLOCK] zone={zone.Id} hero={state.PlayerX},{state.PlayerY} blocks={positions.Count} reachable={initialReach.Count} targets={targets.Count}");
        var nodes = new List<Node> { new(initialHero,initialBlocks,-1,null,initialReach) };
        // Moving blocks cannot open a route through permanent terrain. Reject
        // impossible approaches before spending the bounded arrangement search.
        if (targets.Count>0 && placements.Count==0)
        {
            var optimisticReach=Reach(nodes[0].Hero,Array.Empty<int>());
            if (!targets.Any(t=>In(t.X,t.Y)&&optimisticReach.Contains(Cell(t.X,t.Y))))
            { if(trace) Console.WriteLine($"[BLOCK] permanent terrain separates all targets; optimistic={optimisticReach.Count}"); return null; }
        }
        var visited = new HashSet<string> { Key(nodes[0].Blocks,initialReach) };
        var open=new PriorityQueue<int,double>(); open.Enqueue(0,Priority(nodes[0],initialReach));
        while (open.TryDequeue(out int index,out _))
        {
            var node=nodes[index]; var occupied=node.Blocks.ToHashSet(); var reach=node.Reach;
            bool solved=clearTiles.All(t=>In(t.X,t.Y)&&!occupied.Contains(Cell(t.X,t.Y))) && placements.All(p=>node.Blocks.Select((at,i)=>(at,i)).Any(b=>b.at==Cell(p.X,p.Y)&&tiles[b.i]==p.Tile)) &&
                (targets.Count==0 || targets.Any(t=>In(t.X,t.Y)&&reach.Contains(Cell(t.X,t.Y))));
            if (solved)
            {
                var solution=new List<Step>();
                for(int n=index;nodes[n].Parent>=0;n=nodes[n].Parent) solution.Add(nodes[n].Step!);
                solution.Reverse();
                if (solution.Count > 0 && targets.FirstOrDefault(t => In(t.X,t.Y) && reach.Contains(Cell(t.X,t.Y))) is var target &&
                    targets.Any(t => t == target))
                    solution.Add(new(target.X,target.Y,Direction.Down,false,Map(node.Blocks),Map(node.Blocks),true));
                return solution;
            }
            for(int i=0;i<node.Blocks.Length;i++) foreach(var (dir,dx,dy) in dirs) foreach(bool pull in new[]{false,true})
            {
                if(nodes.Count>=maxStates) continue;
                int x=node.Blocks[i]%w,y=node.Blocks[i]/w;
                int standX=x+(pull?dx:-dx),standY=y+(pull?dy:-dy);
                int endX=x+dx,endY=y+dy,heroX=pull?x+2*dx:x,heroY=pull?y+2*dy:y;
                if(!In(standX,standY)||!In(endX,endY)||!In(heroX,heroY)||!reach.Contains(Cell(standX,standY))||
                    !Open(Cell(endX,endY),occupied,true)||pull&&!Open(Cell(heroX,heroY),occupied)) continue;
                var next=(int[])node.Blocks.Clone(); next[i]=Cell(endX,endY);
                int hero=Cell(heroX,heroY); var nextReach=Reach(hero,next);
                if(!visited.Add(Key(next,nextReach))) continue;
                var step=new Step(x,y,dir,pull,Map(node.Blocks),Map(next));
                // Uncovering a reward in a narrow corridor may require pulling
                // its cover away several times before walking back around it.
                // Prefer continuing that evacuation over shuffling unrelated
                // furniture merely because it temporarily gets closer to the item.
                int moveCost=coveringBlocks.Count>0 && !coveringBlocks.Contains(i) ||
                    placements.Count>0 && !placements.Any(p=>p.Tile==tiles[i]) ? 8 : 1;
                // Opening an approach may first require moving a nearby crate
                // away from the target. Prefer that preparation over cheap
                // permutations of distant furniture without pruning either.
                if(prioritizeNearby)
                    moveCost+=Math.Min(16,goalDistance[node.Blocks[i]]);
                nodes.Add(new(hero,next,index,step,nextReach,node.Cost+moveCost));
                open.Enqueue(nodes.Count-1,Priority(nodes[^1],nextReach));
            }
        }
        if(trace) Console.WriteLine($"[BLOCK] no solution after {nodes.Count} states (limit {maxStates})");
        return null;
    }
}
