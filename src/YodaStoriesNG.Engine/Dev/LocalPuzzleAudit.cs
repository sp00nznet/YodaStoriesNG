using System.Text.Json;
using YodaStoriesNG.Engine.Bot;
using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    public static void TestLocalPuzzlePlans()
    {
        static void Require(bool condition,string message)
        {if(!condition) throw new InvalidOperationException(message);}
        static GameEngine Scene()
        {
            var data=new GameData {GameType=GameType.IndianaJones};
            data.Tiles.AddRange(new[] {new Tile {Flags=TileFlags.Floor},new Tile {Flags=TileFlags.Object},
                new Tile {Flags=TileFlags.Item},new Tile {Flags=TileFlags.Item},
                new Tile {Flags=TileFlags.Object|TileFlags.Draggable}});
            var zone=new Zone {Id=0,Width=7,Height=7,Type=ZoneType.Use,TileGrid=new ushort[7,7,3]};
            for(int y=0;y<7;y++) for(int x=0;x<7;x++)
            {zone.SetTile(x,y,1,ushort.MaxValue);zone.SetTile(x,y,2,ushort.MaxValue);}
            data.Zones.Add(zone);
            var world=new WorldMap {Seed=42,StartingZoneId=0,Mission=new Mission {
                PuzzleChain={new PuzzleStep {ZoneId=0,RequiredItemId=3,RewardItemId=2}} }};
            world.Sectors[0]=new Sector {Zone=0,RequiredItem=3,FindItem=2,Type=ZoneType.Use};
            var e=new GameEngine("") {_gameData=data,_headless=true,_showingTitleScreen=false,_worldGenerator=new WorldGenerator(data)};
            e._worldGenerator.SetCurrentWorld(world);e._state.CurrentZone=zone;e._state.PlayerX=2;e._state.PlayerY=2;
            e._state.Inventory.Add(3);e._state.VisitedZones.Add(0);e._state.SetVariable(1000,1);
            e._sectorPuzzles=new SectorPuzzles(data,e._state,world);e._sectorPuzzles.RestoreLaidDown([0]);
            e.InitializeActionExecutor();e._actionExecutor!.CurrentWorld=world;e._actionExecutor.CurrentMission=world.Mission;
            return e;
        }
        static Data.Action Trade() => new() {
            Conditions={new Condition {Opcode=ConditionOpcode.PlacedItemIs,Arguments={3,2,1,1,3}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.RemoveItem,Arguments={3}},
                new Instruction {Opcode=InstructionOpcode.Wait},new Instruction {Opcode=InstructionOpcode.AddItem,Arguments={2}},
                new Instruction {Opcode=InstructionOpcode.DisableAction}} };

        using var scene=Scene();var zone=scene._state.CurrentZone!;zone.SetTile(3,2,1,1);zone.Actions.Add(Trade());
        var world=scene._worldGenerator!.CurrentWorld!;
        string before=LocalPuzzleState.Signature(scene._gameData!,scene._state,world,[0],returns:scene._doorReturnPoints);
        var result=scene.SearchLocalPuzzle(new(LocalPuzzleGoalKind.AcquireItem,0,2),100,TimeSpan.FromSeconds(10));
        Require(result.Status==LocalPuzzleStatus.Verified,$"local trade not solved: {result.Status}: {result.Detail}");
        Require(result.Plan!.ReplayVerified && result.Plan.Steps.Length>0,"unreplayed local plan accepted");
        Require(LocalPuzzleState.Signature(scene._gameData!,scene._state,world,[0],returns:scene._doorReturnPoints)==before,
            "offline search mutated the source engine");
        var saved=JsonSerializer.Deserialize<LocalPuzzlePlan>(JsonSerializer.Serialize(result.Plan,LocalPuzzleState.Json),LocalPuzzleState.Json)!;
        var solver=new MissionSolver(scene._state,scene._gameData!,scene._worldGenerator!) {LocalPlanner=_=>result};
        var instruction=solver.GetCurrentObjective();
        Require(instruction.Type==saved.Steps[0].Instruction.Type && instruction.TargetX==saved.Steps[0].Instruction.TargetX,
            "the global solver did not select the verified local plan");
        foreach(var step in saved.Steps)
        {
            Require(LocalPuzzleState.Signature(scene._gameData!,scene._state,world,[0],returns:scene._doorReturnPoints)==step.Before,"replay precondition mismatch");
            Require(scene.ReplayPuzzleInstruction(step.Instruction,[0]),"real local executor rejected verified step");
            Require(LocalPuzzleState.Signature(scene._gameData!,scene._state,world,[0],returns:scene._doorReturnPoints)==step.After,"replay postcondition mismatch");
        }
        Require(scene._state.HasItem(2) && !scene._state.HasItem(3),"trade consumed input without giving reward");
        Require(scene._state.PlayerX==2 && scene._state.PlayerY==2,"trade plan did not preserve a return to its entry");
        Require(scene.VerifyPuzzlePlan(saved)==false,"a cached plan was accepted from a different initial state");

        using var running=Scene();running._state.CurrentZone!.SetTile(3,2,1,1);
        running._state.CurrentZone.Actions.Add(Trade());
        // The active mission is elsewhere and needs this room's reward first.
        // This must exercise intermediate-item planning, not the main-step hook.
        var elsewhere=new Zone {Id=1,Width=7,Height=7,Type=ZoneType.Use,
            TileGrid=(ushort[,,])running._state.CurrentZone.TileGrid.Clone()};
        running._gameData!.Zones.Add(elsewhere);
        var runningWorld=running._worldGenerator!.CurrentWorld!;
        runningWorld.Sectors[1]=new Sector {Zone=1,RequiredItem=2,FindItem=4,Type=ZoneType.Use};
        runningWorld.Mission!.PuzzleChain.Clear();
        runningWorld.Mission.PuzzleChain.Add(new PuzzleStep {ZoneId=1,RequiredItemId=2,RewardItemId=4});
        bool intermediateRequested=false;
        running._bot=new MissionBot(running._state,running._gameData!,running._worldGenerator!,running.BotActionPending,
            (z,x,y)=>!running.IsGroundBlocked(z,x,y) && !running.IsScriptedActorTile(z,x,y),goal=> {
                intermediateRequested|=goal is {Sector:0,Item:2};
                return running.PlanLocalPuzzle(goal);
            })
            {DebugLogRoot=Path.Combine(Path.GetTempPath(),"ysng-local-plan-selftest")};
        running._bot.OnActionRequested+=running.HandleBotAction;
        running.EnableBot();
        bool completed=false;
        for(int tick=0;tick<600 && running.IsBotRunning;tick++)
        {
            if(running._messages.HasDialogue) running._messages.DismissDialogue();
            running.Update(1.0/60);
            if(running._state.HasItem(2) && running._state.PlayerX==2 && running._state.PlayerY==2)
            {completed=true;break;}
        }
        Require(completed && intermediateRequested,"the live bot could not execute its intermediate coordinate-only item plan");
        running.DisableBot();

        using var deferred=Scene();deferred._state.CurrentZone!.SetTile(3,2,1,1);
        deferred._state.CurrentZone.Actions.Add(Trade());
        var liveSolver=new MissionSolver(deferred._state,deferred._gameData!,deferred._worldGenerator!) {
            LocalPlanner=_=>new(LocalPuzzleStatus.NeedsLiveActors,"defer to live execution",0,0) };
        liveSolver.RestoreReplayIntent(new MissionSolver.ReplayIntent {ItemRouteFocus=2});
        var liveStep=liveSolver.GetCurrentObjective();
        Require(liveStep.Type==ObjectiveType.UseItemOnNpc && liveStep.RequiredItemId==3,
            "deferring replay lost the live intermediate item dependency");

        using var alternatives=Scene();alternatives._state.CurrentZone!.SetTile(3,2,1,1);
        alternatives._state.CurrentZone.Actions.Add(new Data.Action {Conditions={new Condition {
            Opcode=ConditionOpcode.PlacedItemIs,Arguments={3,2,1,1,4}}},Instructions={
            new Instruction {Opcode=InstructionOpcode.SetRandom,Arguments={30}}}});
        alternatives._state.CurrentZone.Actions.Add(new Data.Action {Conditions={new Condition {
            Opcode=ConditionOpcode.Bump,Arguments={3,2,1}}},Instructions={
            new Instruction {Opcode=InstructionOpcode.SetRandom,Arguments={30}}}});
        alternatives._state.CurrentZone.Actions.Add(new Data.Action {Conditions={new Condition {
            Opcode=ConditionOpcode.RandomIs,Arguments={30}}},Instructions={
            new Instruction {Opcode=InstructionOpcode.DropItem,Arguments={2,2,2}}}});
        var choosing=new MissionSolver(alternatives._state,alternatives._gameData!,alternatives._worldGenerator!);
        choosing.RestoreReplayIntent(new MissionSolver.ReplayIntent {ItemRouteFocus=2});
        var choice=choosing.GetCurrentObjective();
        Require(choice.Type==ObjectiveType.BumpTarget && choice.TargetX==3 && choice.TargetY==2,
            "signal dependency fetched a missing optional item instead of using a ready free interaction");

        using var diagonal=Scene();var corridor=diagonal._state.CurrentZone!;
        for(int y=0;y<7;y++) for(int x=0;x<7;x++) corridor.SetTile(x,y,1,1);
        foreach(var (x,y) in new[]{(2,2),(3,3),(4,3)}) corridor.SetTile(x,y,1,ushort.MaxValue);
        var cardinal=new Pathfinder(diagonal._gameData!,diagonal._state);
        Require(cardinal.FindPath(corridor,2,2,4,3)==null,"diagonal regression corridor unexpectedly had a cardinal route");
        var discrete=new Pathfinder(diagonal._gameData!,diagonal._state) {AllowDiagonal=true,PreferCardinal=true,
            AdditionalWalkability=(z,x,y)=>!diagonal.IsGroundBlocked(z,x,y)};
        Require(WorldActionGraph.WalkableArea(diagonal._gameData!,corridor,2,2,diagonal._state).Contains((4,3)),
            "the world graph lost a diagonal-only tile-step connection");
        var walking=new BotActions(diagonal._state,diagonal._gameData!,discrete) {MovementPending=diagonal.BotActionPending};
        walking.OnAction+=diagonal.HandleBotAction;
        Require(walking.WalkToPosition(4,3),"the live bot could not plan a diagonal-only tile-step route");
        for(int tick=0;tick<600 && !walking.IsCompleted;tick++)
        {diagonal.Update(1.0/60);walking.Update(1.0/60);}
        Require(diagonal._state.PlayerX==4 && diagonal._state.PlayerY==3 && !diagonal._state.SmoothMovementEnabled &&
            diagonal._playerStep==null,"diagonal tile-step execution failed or enabled smooth movement");
        var actor=new NPC {X=3,Y=3,IsEnabled=true,Health=1};
        Require(discrete.FindPath(corridor,2,2,4,3,[actor])==null,"diagonal tile-step route ignored an actor blocking the destination");
        diagonal._state.SmoothMovementEnabled=true;
        Require(discrete.FindPath(corridor,2,2,4,3)==null,"smooth routing crossed opaque corners rejected by the movement handler");

        using var variant=Scene();variant._state.CurrentZone!.SetTile(3,2,1,1);
        var guarded=Trade();guarded.Conditions.Add(new Condition {Opcode=ConditionOpcode.RandomIs,Arguments={1}});
        variant._state.CurrentZone.Actions.Add(guarded);variant._state.ZoneRandom[0]=2;
        Require(variant.SearchLocalPuzzle(new(LocalPuzzleGoalKind.AcquireItem,0,2),30,TimeSpan.FromSeconds(3)).Status!=LocalPuzzleStatus.Verified,
            "plan invented the unselected random variant");
        variant._state.ZoneNPCs.Add(new NPC {IsEnabled=true,IsHostile=true,Health=10});
        var live=variant.SearchLocalPuzzle(new(LocalPuzzleGoalKind.AcquireItem,0,2),30,TimeSpan.FromSeconds(3));
        Require(live.Status==LocalPuzzleStatus.NeedsLiveActors && live.Deferred is {Length:1} && live.Deferred[0].Health==10,
            "offline search invented live combat");
        string ActorState()=>LocalPuzzleState.Signature(variant._gameData!,variant._state,
            variant._worldGenerator!.CurrentWorld!,[0]);
        string actorBefore=ActorState();variant._state.ZoneNPCs[0].X++;
        Require(actorBefore!=ActorState(),"enemy relocation did not invalidate the plan contract");
        actorBefore=ActorState();variant._state.ZoneNPCs[0].Behavior=NPCBehavior.Stationary;
        Require(actorBefore!=ActorState(),"changing actor behavior did not invalidate the plan contract");
        variant._state.CurrentZone.Monsters.Add(new ZoneMonster {X=1,Y=1});
        actorBefore=ActorState();variant._state.CurrentZone.Monsters[0].X=2;
        Require(actorBefore!=ActorState(),"changing a room's monster placement did not invalidate the plan contract");
        actorBefore=ActorState();variant._state.ZoneNPCs[0].Health=0;
        Require(actorBefore!=ActorState(),"defeating an enemy did not invalidate the plan contract");
        variant._state.ZoneRandom[0]=1;
        Require(variant.SearchLocalPuzzle(new(LocalPuzzleGoalKind.AcquireItem,0,2),50,TimeSpan.FromSeconds(5)).Status==LocalPuzzleStatus.Verified,
            "the local solver did not reconsider the puzzle after live combat ended");

        using var trap=Scene();var trapped=trap._state.CurrentZone!;trapped.SetTile(3,2,1,1);
        foreach(var (x,y) in new[]{(4,4),(5,4),(6,4),(4,5),(6,5),(4,6),(5,6),(6,6)}) trapped.SetTile(x,y,1,1);
        trapped.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.Bump,Arguments={3,2,1}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.MoveHeroTo,Arguments={5,5}},
                new Instruction {Opcode=InstructionOpcode.AddItem,Arguments={2}},new Instruction {Opcode=InstructionOpcode.DisableAction}}});
        Require(trap.SearchLocalPuzzle(new(LocalPuzzleGoalKind.AcquireItem,0,2),50,TimeSpan.FromSeconds(5)).Status!=LocalPuzzleStatus.Verified,
            "a reward that strands the player was accepted as a reusable puzzle solution");

        using var blocks=Scene();blocks._state.PlayerY=3;blocks._state.CurrentZone!.SetTile(3,3,1,4);
        blocks._state.CurrentZone.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.TileAtIs,Arguments={4,4,3,1}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.WinGame}}});
        var pushed=blocks.SearchLocalPuzzle(new(LocalPuzzleGoalKind.Win,0),60,TimeSpan.FromSeconds(5));
        Require(pushed.Status==LocalPuzzleStatus.Verified && pushed.Plan!.Steps.Any(s=>s.Instruction.Type==ObjectiveType.PushObject),
            $"cardinal block solution was not replayed: {pushed.Status}");
        string blockState=LocalPuzzleState.Signature(blocks._gameData!,blocks._state,blocks._worldGenerator!.CurrentWorld!,[0]);
        blocks._state.CurrentZone.SetTile(3,3,1,ushort.MaxValue);blocks._state.CurrentZone.SetTile(4,3,1,4);
        Require(blockState!=LocalPuzzleState.Signature(blocks._gameData!,blocks._state,blocks._worldGenerator.CurrentWorld!,[0]),
            "moving a block did not invalidate its puzzle state contract");

        using var transparent=Scene();
        var transparentArea=transparent._state.CurrentZone!;
        transparent._gameData!.Tiles.Add(new Tile {Flags=TileFlags.Floor|TileFlags.Transparency});
        for(int y=0;y<7;y++) for(int x=0;x<7;x++) transparentArea.SetTile(x,y,1,1);
        transparentArea.SetTile(1,2,1,5);transparentArea.SetTile(2,2,1,4);transparentArea.SetTile(3,2,1,ushort.MaxValue);
        transparent._state.PlayerX=1;transparent._state.PlayerY=2;
        var transparentPlan=BlockPuzzlePlanner.Plan(transparent._gameData,transparentArea,transparent._state,
            new[]{(2,2)},Array.Empty<BlockPuzzlePlanner.Placement>());
        Require(transparentPlan is {Count:>0},"object search rejected a walkable transparent standing tile");
        foreach(var move in transparentPlan!)
            Require(transparent.ReplayPuzzleInstruction(new BotObjective {
                Type=move.Walk ? ObjectiveType.WalkToPosition : move.Pull ? ObjectiveType.PullObject : ObjectiveType.PushObject,
                TargetX=move.X,TargetY=move.Y,Direction=move.Direction},[0]),"transparent standing plan failed live replay");

        using var backing=Scene();var backingArea=backing._state.CurrentZone!;
        backing._gameData!.Tiles.Add(new Tile {Flags=TileFlags.Floor|TileFlags.Transparency});
        for(int y=0;y<7;y++) for(int x=0;x<7;x++) backingArea.SetTile(x,y,1,1);
        foreach(var (x,y) in new[]{(2,2),(3,3),(2,3),(1,3)}) backingArea.SetTile(x,y,1,ushort.MaxValue);
        backingArea.SetTile(1,2,1,4);backingArea.SetTile(3,2,1,5);
        backingArea.Objects.Add(new ZoneObject {Type=ZoneObjectType.Trigger,X=1,Y=2,Argument=2});
        backing._state.PlayerX=2;backing._state.PlayerY=2;
        var backingWorld=backing._worldGenerator!.CurrentWorld!;
        backingWorld.Mission!.PuzzleChain[0].RequiredItemId=2;
        var backingSolver=new MissionSolver(backing._state,backing._gameData,backing._worldGenerator!);
        var backingMove=backingSolver.GetCurrentObjective();
        Require(backingMove?.Type==ObjectiveType.PullObject,"live object validation rejected a transparent walkable surface");
        Require(backing.ReplayPuzzleInstruction(backingMove!,[0]) && backing._state.PlayerX==3 && backing._state.PlayerY==2,
            "pull onto transparent walkable surface failed in the engine");

        using var introduction=Scene();var introArea=introduction._state.CurrentZone!;
        introArea.SetTile(4,2,1,2);
        introArea.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.Bump,Arguments={4,2,2}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.SpeakNpc,Arguments={4,2},Text="Please take me with you."},
                new Instruction {Opcode=InstructionOpcode.DisableAction}}});
        var introSolver=new MissionSolver(introduction._state,introduction._gameData!,introduction._worldGenerator!,
            (z,x,y)=>!introduction.IsScriptedActorTile(z,x,y));
        var introWorld=introduction._worldGenerator!.CurrentWorld!;
        introSolver.GetCurrentObjective();
        var intro=introSolver.NativeGoalPrerequisite(introWorld,introWorld.Sectors[0],rewardItem:2);
        Require(intro?.Type==ObjectiveType.BumpTarget,"pickup planning skipped a self-disabling introduction");
        Require(introduction.ReplayPuzzleInstruction(intro!,[0]) && introduction._state.DisabledActions.Contains((0,0)),
            "pickup introduction did not disable its dialogue");
        Require(introduction.ReplayPuzzleInstruction(new BotObjective {Type=ObjectiveType.PickupItem,TargetX=4,TargetY=2},[0]) &&
            introduction._state.HasItem(2),"introduced character could not be collected");

        using var crossing=Scene();var crossingArea=crossing._state.CurrentZone!;
        for(int y=0;y<7;y++) for(int x=0;x<7;x++) crossingArea.SetTile(x,y,1,1);
        crossingArea.SetTile(2,2,1,ushort.MaxValue);crossingArea.SetTile(4,2,1,2);
        crossingArea.Actions.Add(new Data.Action {
            Conditions={new Condition {Opcode=ConditionOpcode.PlacedItemIs,Arguments={2,3,1,1,3}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.MoveHeroTo,Arguments={4,2}}}});
        var crossingSolver=new MissionSolver(crossing._state,crossing._gameData!,crossing._worldGenerator!);
        crossingSolver.GetCurrentObjective();
        var crossingStep=crossingSolver.GoalRouteOpening([crossingArea],0,4,2,false);
        Require(crossingStep is {Action:0},"same-room route lost its authored passage action");

        using var dense=Scene();
        string[] layout=["#########","#b.b#.bb#","#bbbbbbb#","#bbbbbb.#","#.bb..b.#",
            "#.b.bbb.#","#b..bbb.#","#b......#","####.####"];
        var furniture=new Zone {Id=0,Width=9,Height=9,TileGrid=new ushort[9,9,3]};
        for(int y=0;y<9;y++) for(int x=0;x<9;x++) {
            furniture.SetTile(x,y,1,layout[y][x]=='#' ? (ushort)1 : layout[y][x]=='b' ? (ushort)4 : ushort.MaxValue);
            furniture.SetTile(x,y,2,ushort.MaxValue);
        }
        dense._gameData!.Zones[0]=furniture;dense._state.CurrentZone=furniture;
        dense._state.PlayerX=4;dense._state.PlayerY=7;
        var access=BlockPuzzlePlanner.Plan(dense._gameData,furniture,dense._state,
            Pathfinder.InteractionCells(4,1,false),Array.Empty<BlockPuzzlePlanner.Placement>());
        Require(access is {Count:>0},"bounded object search could not open an approach through dense furniture");
        foreach(var step in access!) {
            Require(BlockPuzzlePlanner.Layout(furniture)==step.Before,"dense object plan precondition changed");
            Require(dense.ReplayPuzzleInstruction(new BotObjective {
                Type=step.Walk ? ObjectiveType.WalkToPosition : step.Pull ? ObjectiveType.PullObject : ObjectiveType.PushObject,
                TargetX=step.X,TargetY=step.Y,Direction=step.Direction},[0]),"dense object plan did not execute");
            Require(BlockPuzzlePlanner.Layout(furniture)==step.After,"dense object plan did not reproduce its planned layout");
        }
        Require(Math.Abs(dense._state.PlayerX-4)+Math.Abs(dense._state.PlayerY-1)==1,
            "dense object plan failed to reach a cardinal chest interaction cell");

        var catalogue=PuzzleCatalogue.Compile(blocks._gameData!);
        Require(catalogue.Templates.Single().Mechanics.Contains("push-pull") &&
            catalogue.Templates.Single().Mechanics.Contains("authored-victory"),"catalogue omitted local mechanics");
        using var stale=Scene();stale._state.CurrentZone!.SetTile(3,2,1,1);stale._state.CurrentZone.Actions.Add(Trade());
        stale._state.SetCounter(0,9);
        var staleSolver=new MissionSolver(stale._state,stale._gameData!,stale._worldGenerator!) {LocalPlanner=_=>result};
        Require(staleSolver.GetCurrentObjective().Description!=saved.Steps[0].Instruction.Description,
            "the global solver accepted a local plan with stale preconditions");
    }
}
