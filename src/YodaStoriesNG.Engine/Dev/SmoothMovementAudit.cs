using System.Text.Json;
using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

// Real engine paths, without SDL initialization, a window, or on-disk preference changes.
public unsafe partial class GameEngine
{
    public static void TestSmoothMovement()
    {
        static void Require(bool ok, string message)
        { if (!ok) throw new InvalidOperationException(message); }
        static GameEngine Scene()
        {
            var engine = new GameEngine("") { _gameData = new GameData(), _showingTitleScreen = false,
                _smoothMovement = true, _smoothMovementRequested = true };
            engine._gameData.Tiles.Add(new Tile { Flags = TileFlags.Floor });
            engine._gameData.Tiles.Add(new Tile { Flags = TileFlags.Object });
            engine._gameData.Tiles.Add(new Tile { Flags = TileFlags.Object | TileFlags.Draggable });
            engine._gameData.Tiles.Add(new Tile { Flags = TileFlags.Item });
            var zone = new Zone { Id = 0, Width = 18, Height = 18, TileGrid = new ushort[18,18,3] };
            for (int y = 0; y < 18; y++) for (int x = 0; x < 18; x++)
            { zone.SetTile(x, y, 1, ushort.MaxValue); zone.SetTile(x, y, 2, ushort.MaxValue); }
            engine._gameData.Zones.Add(zone);
            engine._state.CurrentZone = zone;
            engine._state.PlayerX = 3; engine._state.PlayerY = 3;
            engine._actionExecutor = new ActionExecutor(engine._gameData, engine._state);
            engine._actionExecutor.OnHeroRelocated += engine.CancelSmoothMovement;
            return engine;
        }

        // Collision and navigation must agree even without an engine callback.
        var terrain = Scene();
        var terrainZone = terrain._state.CurrentZone!;
        terrainZone.SetTile(4, 3, 0, 1);
        var terrainNav = new Bot.Pathfinder(terrain._gameData!, terrain._state);
        Require(terrain.IsGroundBlocked(terrainZone, 4, 3) && !terrainNav.IsWalkable(terrainZone, 4, 3),
            "solid ground was walkable in the engine or planner");
        terrainZone.SetTile(4, 3, 1, 0);
        Require(!terrain.IsGroundBlocked(terrainZone, 4, 3) && terrainNav.IsWalkable(terrainZone, 4, 3),
            "a walkable bridge did not cover solid ground");
        foreach (bool smooth in new[] { false, true })
        {
            var exact = Scene();
            exact._smoothMovement = exact._smoothMovementRequested = smooth;
            exact._gameData!.Tiles[1].Flags |= TileFlags.Transparency;
            exact._bot = new Bot.MissionBot(exact._state, exact._gameData!, new WorldGenerator(exact._gameData!));
            typeof(Bot.MissionBot).GetField("_isRunning", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.SetValue(exact._bot, true);
            exact.ApplyPendingMovementMode();
            Require(!exact._state.SmoothMovementEnabled && exact._smoothMovementRequested == smooth,
                "bot movement changed the user's preference or kept planning smooth steps");
            exact._state.CurrentZone!.SetTile(4, 3, 1, 1);
            Require(!new Bot.Pathfinder(exact._gameData, exact._state).IsWalkable(exact._state.CurrentZone, 4, 3),
                "alpha transparency made a solid object walkable");
            exact.HandleBotAction(Bot.BotActionType.Move, 4, 3, Direction.Right);
            Require(exact._state.PlayerX == 3 && exact._state.PlayerY == 3 && exact._playerStep == null,
                "a rejected bot move slid to a different, unplanned destination");
            typeof(Bot.MissionBot).GetField("_isRunning", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.SetValue(exact._bot, false);
            exact.ApplyPendingMovementMode();
            Require(exact._state.SmoothMovementEnabled == smooth,
                "manual movement did not recover its original setting after the bot stopped");
        }

        // Distances and time, including irregular frame lengths and diagonal normalization.
        foreach (double hz in new[] { 30.0, 60.0, 144.0 })
        {
            var step = new MovementStep(0, 0, 1, 0, 1.0 / 6, Direction.Right);
            double elapsed = 0;
            while (elapsed + 1.0 / hz < step.Duration)
            { step.Advance(1.0 / hz); elapsed += 1.0 / hz; }
            Require(step.Advance(step.Duration - elapsed), $"step did not finish at {hz} Hz");
            Require(Math.Abs(step.X - 1) < 1e-8, "step overshot its destination");
        }
        foreach (double hz in new[] { 30.0, 60.0, 144.0 })
        {
            var walking = Scene();
            walking._smoothInputSource = () => (1, 0, 1);
            for (int tick = 0; tick < (int)hz; tick++) walking.UpdateSmoothMovement(1.0 / hz);
            Require(walking._state.PlayerX == 9, $"held-input speed changed at {hz} Hz: {walking._state.PlayerX}");
        }
        var diagonal = new MovementStep(0, 0, 1, 1, 1.0 / 6, Direction.Down);
        Require(Math.Abs(diagonal.Duration - Math.Sqrt(2) / 6) < 1e-8, "diagonal speed boost");
        Require(diagonal.Conflicts(new MovementStep(1, 0, -1, 1, 1, Direction.Left)), "crossing diagonals were not reserved");
        Require(diagonal.Conflicts(new MovementStep(1, 1, -1, -1, 1, Direction.Left)), "actor swap was not reserved");

        var pausedStep=Scene();
        pausedStep.RequestSmoothPlayerStep(1,0,Direction.Right,false);
        pausedStep.UpdateSmoothMovement(1.0/12);
        var heldStep=pausedStep._playerStep;
        pausedStep._state.CurrentZone!.Actions.Add(new Data.Action {
            Conditions={new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.Wait,Arguments={0} },
                new Instruction { Opcode=InstructionOpcode.DisableAction }} });
        pausedStep._actionExecutor!.ExecuteZoneActions(ActionTrigger.Walk);
        pausedStep.UpdateSmoothMovement(1);
        Require(ReferenceEquals(heldStep,pausedStep._playerStep) && Math.Abs(heldStep!.Elapsed-1.0/12)<1e-8,
            "background script wait discarded or advanced the paused player step");
        pausedStep._actionExecutor.ExecuteZoneActions(ActionTrigger.Walk);
        pausedStep.UpdateSmoothMovement(1.0/12);
        Require(pausedStep._state.PlayerX==4,"paused player step did not resume after a background wait");

        var e = Scene();
        e._state.CurrentZone!.SetTile(4, 3, 1, 3);
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, false);
        e.UpdateSmoothMovement(1.0 / 12);
        Require(e._state.PlayerX == 3 && Math.Abs(e.PlayerRenderPosition.X - 3.5) < 1e-8, "position committed before arrival");
        Require(!e._state.Inventory.Contains(3), "pickup happened before arrival");
        e.UpdateSmoothMovement(1.0 / 12);
        Require(e._state.PlayerX == 4 && e._state.Inventory.Count(i => i == 3) == 1, "arrival or pickup was lost");
        e.UpdateSmoothMovement(1);
        Require(e._state.PlayerX == 4 && e._playerStep == null && e._playerAnimationFrame == 0, "released input kept moving or animating");
        Require(e._state.Inventory.Count(i => i == 3) == 1, "pickup ran twice");

        e = Scene();
        e._state.ZoneNPCs.Add(new NPC { X = 4, Y = 3, Health = 10 });
        Require(!e.RequestSmoothPlayerStep(1, 1, Direction.Right, false), "diagonal cut through an actor");
        Require(e.RequestSmoothPlayerStep(0, 1, Direction.Down, false), "valid cardinal slide was blocked");
        e._smoothMovementRequested = false;
        e.ApplyPendingMovementMode();
        Require(e._smoothMovement, "mode changed during a step");
        e._state.IsPaused = true;
        e.UpdateSmoothMovement(1);
        Require(e._playerStep!.Elapsed == 0, "paused step advanced");
        e._state.IsPaused = false;
        e.UpdateSmoothMovement(1.0 / 6);
        Require(!e._smoothMovement && e._state.PlayerY == 4, "deferred mode change did not finish safely");

        e = Scene();
        e._state.CurrentZone!.SetTile(4, 3, 1, 2);
        Require(e.RequestSmoothPlayerStep(1, 0, Direction.Right, false), "cardinal push could not start");
        Require(e.MovementReserved(5, 3), "block destination was not reserved");
        Require(e._state.CurrentZone.GetTile(4, 3, 1) == 2, "block moved before arrival");
        e.UpdateSmoothMovement(1.0 / 6);
        Require(e._state.CurrentZone.GetTile(5, 3, 1) == 2 && e._state.PlayerX == 4, "push did not finish at arrival");
        e = Scene();
        e._state.CurrentZone!.SetTile(4, 4, 1, 2);
        Require(!e.RequestSmoothPlayerStep(1, 1, Direction.Right, false), "diagonal push was accepted");

        e = Scene();
        var npc = new NPC { X = 7, Y = 7, StartX = 7, StartY = 7, Behavior = NPCBehavior.Chasing };
        e._state.ZoneNPCs.Add(npc);
        e.StartSmoothNPCStep(npc);
        Require(e._npcSteps.TryGetValue(npc, out var route) && route.ToX == 6 && route.ToY == 6, "enemy did not chase diagonally");
        Require(!e.SmoothCellClear(6, 6), "enemy destination was not reserved");
        e.UpdateSmoothMovement(npc.MoveCooldown * Math.Sqrt(2));
        Require(npc.X == 6 && npc.Y == 6, "enemy did not commit its diagonal step");
        Require(ProjectileTouches(new Projectile { X = 6.5, Y = 6.5 }, e.NPCRenderPosition(npc)), "projectile missed interpolated actor");
        Require(!ProjectileTouches(new Projectile { X = 9, Y = 9 }, e.NPCRenderPosition(npc)), "projectile hit distant actor");
        Require(e.GetUsableAnimationFrame(new ushort[] { 0, 1, ushort.MaxValue }, 2) == 1, "missing frame made enemy invisible");

        e = Scene();
        e._state.PlayerX = 8;
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, false);
        e.UpdateSmoothMovement(1.0 / 12);
        Require(Math.Abs(e.ViewCameraX - 4.5) < 1e-8, "camera did not follow the interpolated player");
        e._pendingMovementAction = () => throw new Exception("stale action ran");
        e.CancelSmoothMovement();
        Require(e._playerStep == null && e._pendingMovementAction == null, "movement reset kept stale actions");
        e = Scene();
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, false);
        e.PerformAttack();
        Require(!e._state.IsAttacking && e._pendingMovementAction != null, "attack did not wait for a tile center");
        e.UpdateSmoothMovement(1.0 / 6);
        Require(e._state.IsAttacking && e._pendingMovementAction == null, "queued attack did not execute at arrival");

        e = Scene();
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, false);
        e._state.CurrentZone!.Actions.Add(new Data.Action
        {
            Instructions = { new Instruction { Opcode = InstructionOpcode.MoveHeroTo, Arguments = { 8, 9 } } }
        });
        e._actionExecutor!.ExecuteZoneActions(ActionTrigger.Walk);
        Require(e._playerStep == null && e._state.PlayerX == 8 && e._state.PlayerY == 9,
            "script relocation did not cancel the step");
        e.UpdateSmoothMovement(1);
        Require(e._state.PlayerX == 8 && e._state.PlayerY == 9, "stale movement overwrote a scripted relocation");

        e = Scene();
        e._state.CurrentZone!.Objects.Add(new ZoneObject { X = 4, Y = 4, Type = ZoneObjectType.DoorEntrance, Enabled = true });
        Require(e.RequestSmoothPlayerStep(1, 1, Direction.Down, false), "open doorway rejected its diagonal approach");
        e.CancelSmoothMovement();
        e = Scene();
        e._state.CurrentZone!.Objects.Add(new ZoneObject { X = 4, Y = 4, Type = ZoneObjectType.VehicleToPrimary, Enabled = true });
        Require(!e.RequestSmoothPlayerStep(1, 1, Direction.Down, false), "diagonal boarding was accepted");
        e = Scene();
        e._state.CurrentZone!.Objects.Add(new ZoneObject { X=4,Y=3,Type=ZoneObjectType.DoorEntrance,Argument=1,Enabled=true });
        e._state.CurrentZone.SetTile(4,3,1,3);
        var destination=new Zone { Id=1,Width=18,Height=18,TileGrid=new ushort[18,18,3] };
        for(int y=0;y<18;y++) for(int x=0;x<18;x++)
        { destination.SetTile(x,y,1,ushort.MaxValue); destination.SetTile(x,y,2,ushort.MaxValue); }
        destination.Objects.Add(new ZoneObject { X=1,Y=1,Type=ZoneObjectType.DoorExit,Argument=ushort.MaxValue,Enabled=true });
        e._gameData!.Zones.Add(destination);
        Require(e.RequestSmoothPlayerStep(1,0,Direction.Right,false),"doorway reward step was rejected");
        e.UpdateSmoothMovement(1.0/6);
        Require(e._state.HasItem(3),"travel discarded the loose item on its portal");
        Require(e._state.CurrentZoneId==1,"collecting a portal reward prevented the intended travel");

        foreach (var behavior in new[] { NPCBehavior.Fleeing, NPCBehavior.Patrol })
        {
            e = Scene();
            var moving = new NPC { X = 7, Y = 7, Behavior = behavior, Waypoints = new[] { (9, 9) } };
            e._state.ZoneNPCs.Add(moving);
            e.StartSmoothNPCStep(moving);
            Require(e._npcSteps.TryGetValue(moving, out var path) && path.ToX == 8 && path.ToY == 8,
                $"enemy {behavior} did not use a diagonal route");
        }
        e = Scene();
        e._smoothMovement = e._smoothMovementRequested = false;
        e.TryMovePlayer(1, 0, Direction.Right);
        Require(e._state.PlayerX == 4 && e._playerStep == null, "classic mode did not move immediately");
        e = Scene();
        e._state.CurrentZone!.SetTile(2, 3, 1, 2);
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, true);
        e.UpdateSmoothMovement(1.0 / 6);
        Require(e._state.CurrentZone.GetTile(3, 3, 1) == 2 && e._state.CurrentZone.GetTile(2, 3, 1) == ushort.MaxValue,
            "cardinal pulling did not move the trailing block");

        e = Scene();
        int health = e._state.Health;
        e._state.CurrentZone!.Actions.Add(new Data.Action
        {
            Conditions = { new Condition { Opcode = ConditionOpcode.Standing, Arguments = { 4, 3, 0 } } },
            Instructions = { new Instruction { Opcode = InstructionOpcode.AddHealth, Arguments = { 1 } },
                new Instruction { Opcode = InstructionOpcode.DisableAction } }
        });
        e._state.Health -= 10; health = e._state.Health;
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, false);
        e.UpdateSmoothMovement(1.0 / 12);
        Require(e._state.Health == health, "entry script ran before arrival");
        e.UpdateSmoothMovement(1.0 / 12);
        e.Update(1.0 / 60);
        Require(e._state.Health == health + 1, "entry script did not run exactly once at arrival");

        e = Scene();
        e.RequestSmoothPlayerStep(1, 0, Direction.Right, false);
        var opposing = new NPC { X = 5, Y = 3, Behavior = NPCBehavior.Chasing };
        e._state.ZoneNPCs.Add(opposing);
        e.StartSmoothNPCStep(opposing);
        Require(!e._npcSteps.ContainsKey(opposing), "enemy entered the player's reserved destination");
        e._movementFocused = false;
        e.CancelSmoothMovement();
        e.UpdateSmoothMovement(1);
        Require(e._state.PlayerX == 3 && e._playerStep == null, "focus loss committed a stale step");
        e = Scene();
        e._state.CurrentZone!.SetTile(2,3,1,2);
        bool shift = false;
        e._smoothInputSource = () => (1,0,1);
        e._smoothPullSource = () => shift;
        e.UpdateSmoothMovement(0.05);
        Require(e._playerStep is { Pull: false }, "late-Shift fixture did not start an ordinary step");
        shift = true;
        e.UpdateSmoothMovement(1.0 / 6 - 0.05);
        Require(e._state.PlayerX == 4 && e._state.CurrentZone.GetTile(3,3,1) == 2 &&
            e._state.CurrentZone.GetTile(2,3,1) == ushort.MaxValue, "Shift pressed during a step failed to pull at arrival");

        e = Scene();
        e._state.CurrentZone!.SetTile(2,2,1,2);
        e.RequestSmoothPlayerStep(1,1,Direction.Right,false);
        e._smoothPullSource = () => true;
        e.UpdateSmoothMovement(Math.Sqrt(2) / 6);
        Require(e._state.CurrentZone.GetTile(2,2,1) == 2 && e._state.CurrentZone.GetTile(3,3,1) == ushort.MaxValue,
            "late Shift pulled a block diagonally");
        foreach(bool smooth in new[] { false,true })
        {
            e=Scene(); e._smoothMovement=smooth;
            e._gameData!.Characters.Add(new Character { Type=CharacterType.Enemy,MovementType=11,
                Weapon=new CharacterWeapon { Reference=-1,Health=ushort.MaxValue } });
            var fugitive=new NPC { CharacterId=0,X=4,Y=3 };
            e.ConfigureNPCFromCharacter(fugitive); e._state.ZoneNPCs.Add(fugitive);
            Require(fugitive.Behavior==NPCBehavior.Fleeing && fugitive.IsInvulnerable,
                "authored fleeing actor lost its movement or invulnerability");
            e._state.CurrentZone!.SetTile(5,3,1,1);
            if(smooth)
            {
                e.StartSmoothNPCStep(fugitive);
                Require(e._npcSteps.TryGetValue(fugitive,out var escape) && escape.ToY!=3 && escape.ToX>=4,
                    "fleeing actor remained trapped against a wall instead of taking a clear side step");
            }
            else
            {
                e.UpdateFleeingNPC(fugitive);
                Require(fugitive.X>=4 && fugitive.Y!=3,"tile movement did not use a clear fleeing side step");
            }
        }
        var legacy = JsonSerializer.Deserialize<KeyBindings>("{}");
        var enabled = JsonSerializer.Deserialize<KeyBindings>(JsonSerializer.Serialize(new KeyBindings { SmoothMovement = true }));
        Require(legacy?.SmoothMovement == false && enabled?.SmoothMovement == true, "settings round trip or legacy default failed");
    }
}
