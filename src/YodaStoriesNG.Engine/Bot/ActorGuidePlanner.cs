using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

// Search actor positions and the player's reachable guiding stances. Each edge
// predicts one ordinary fleeing step; execution replans when the actor moves.
internal static class ActorGuidePlanner
{
    internal static BotObjective? Plan(GameData data,GameState state,NPC actor,int x,int y,int tile,
        Func<Zone,int,int,bool>? canWalk=null)
    {
        if(tile<=0 || tile>=data.Tiles.Count || state.CurrentZone is not { } zone || actor.Behavior!=NPCBehavior.Fleeing ||
            !actor.IsAlive || !actor.IsEnabled || actor.CharacterId<0 || actor.CharacterId>=data.Characters.Count) return null;
        var character=data.Characters[actor.CharacterId];
        if(!Enum.GetValues<Direction>().Any(d=>ScriptedActorTiles.Frames(character,d).Contains((ushort)tile))) return null;
        var nav=new Pathfinder(data,state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=canWalk };
        var other=state.ZoneNPCs.Where(n=>n!=actor && n.IsAlive && n.IsEnabled).ToList();
        var first=(AX:actor.X,AY:actor.Y,PX:state.PlayerX,PY:state.PlayerY,Dir:actor.Direction);
        var pending=new PriorityQueue<((int AX,int AY,int PX,int PY,Direction Dir) At,int SX,int SY,int NX,int NY,int Steps),int>();
        pending.Enqueue((first,-1,-1,-1,-1,0),0);
        var seen=new HashSet<(int AX,int AY,int PX,int PY,Direction Dir)>();
        int closest=int.MaxValue;
        while(pending.TryDequeue(out var entry,out _) && seen.Count<5000)
        {
            var at=entry.At;
            if(!seen.Add(at)) continue;
            closest=Math.Min(closest,Math.Abs(at.AX-x)+Math.Abs(at.AY-y));
            if(at.AX==x && at.AY==y && entry.Steps>0 && ScriptedActorTiles.Frames(character,at.Dir).Contains((ushort)tile))
                return new BotObjective { Type=ObjectiveType.GuideActor,TargetNpc=actor,TargetX=actor.X,TargetY=actor.Y,
                    ApproachX=entry.SX,ApproachY=entry.SY,ArrivalX=entry.NX,ArrivalY=entry.NY,
                    Description=$"Guide actor {actor.NativeMonsterIndex} toward scripted tile ({x},{y})" };
            var blockers=other.Concat(new[] { new NPC { X=at.AX,Y=at.AY,Behavior=NPCBehavior.Stationary } }).ToList();
            var stances=Pathfinder.InteractionCells(at.AX,at.AY,true).Prepend((at.PX,at.PY)).Distinct();
            foreach(var stance in stances)
            {
                if(nav.FindPath(zone,at.PX,at.PY,stance.Item1,stance.Item2,blockers)==null) continue;
                int dx=at.AX-stance.Item1,dy=at.AY-stance.Item2,sx=Math.Sign(dx),sy=Math.Sign(dy);
                var candidates=new List<(int X,int Y)> { (sx,sy) };
                if(Math.Abs(dx)>=Math.Abs(dy)) { candidates.Add((sx,0)); candidates.Add((0,sy)); }
                else { candidates.Add((0,sy)); candidates.Add((sx,0)); }
                candidates.AddRange(GameEngine.FleeAlternatives(at.AX,at.AY,stance.Item1,stance.Item2));
                bool moved=false;
                foreach(var step in candidates.Distinct())
                {
                    int nx=at.AX+step.X,ny=at.AY+step.Y;
                    bool SideClear(int cx,int cy)=>cx>=0 && cy>=0 && cx<zone.Width && cy<zone.Height &&
                        (cx,cy)!=stance && !other.Any(n=>n.X==cx && n.Y==cy);
                    if(step==(0,0) || !nav.IsWalkable(zone,nx,ny,other) || (nx,ny)==stance ||
                        step.X!=0 && step.Y!=0 && (!SideClear(at.AX+step.X,at.AY) || !SideClear(at.AX,at.AY+step.Y))) continue;
                    var horizontal=step.X<0 ? Direction.Left : Direction.Right;
                    var vertical=step.Y<0 ? Direction.Up : Direction.Down;
                    var facing=step.X==0 ? vertical : step.Y==0 ? horizontal :
                        at.Dir==horizontal || at.Dir==vertical ? at.Dir : horizontal;
                    // Starting a step turns the actor before its committed cell
                    // changes. Native picture guards can react during that turn.
                    if(at.AX==x && at.AY==y && ScriptedActorTiles.Frames(character,facing).Contains((ushort)tile))
                        return new BotObjective { Type=ObjectiveType.GuideActor,TargetNpc=actor,TargetX=actor.X,TargetY=actor.Y,
                            ApproachX=entry.Steps==0 ? stance.Item1 : entry.SX,
                            ApproachY=entry.Steps==0 ? stance.Item2 : entry.SY,
                            ArrivalX=entry.Steps==0 ? nx : entry.NX,ArrivalY=entry.Steps==0 ? ny : entry.NY,
                            Description=$"Guide actor {actor.NativeMonsterIndex} to face the scripted trigger at ({x},{y})" };
                    var next=(nx,ny,stance.Item1,stance.Item2,facing);
                    int count=entry.Steps+1;
                    pending.Enqueue((next,entry.Steps==0 ? stance.Item1 : entry.SX,entry.Steps==0 ? stance.Item2 : entry.SY,
                        entry.Steps==0 ? nx : entry.NX,entry.Steps==0 ? ny : entry.NY,count),count+Math.Abs(nx-x)+Math.Abs(ny-y));
                    moved=true;
                    break;
                }
                // A trapped native actor turns toward an aligned player.
                // These poses are authored puzzle guards as well as artwork.
                if(!moved && at.AX==x && at.AY==y && (dx==0 || dy==0) && (dx!=0 || dy!=0))
                {
                    var facing=dx==0 ? dy>0 ? Direction.Up : Direction.Down : dx>0 ? Direction.Left : Direction.Right;
                    if(ScriptedActorTiles.Frames(character,facing).Contains((ushort)tile))
                        return new BotObjective { Type=ObjectiveType.GuideActor,TargetNpc=actor,TargetX=actor.X,TargetY=actor.Y,
                            ApproachX=entry.Steps==0 ? stance.Item1 : entry.SX,ApproachY=entry.Steps==0 ? stance.Item2 : entry.SY,
                            ArrivalX=entry.Steps==0 ? at.AX : entry.NX,ArrivalY=entry.Steps==0 ? at.AY : entry.NY,
                            Description=$"Guide actor {actor.NativeMonsterIndex} into the authored trap at ({x},{y})" };
                }
            }
        }
        Console.WriteLine($"[BOT] No actor guidance route to ({x},{y}) picture {tile}: explored {seen.Count} states, closest distance {closest}");
        return null;
    }
}
