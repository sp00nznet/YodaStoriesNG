using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    public static void TestAreaReset()
    {
        static void Require(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
        static GameEngine Scene(bool smooth = false)
        {
            var e = new GameEngine("") { _gameData = new GameData(), _smoothMovement = smooth,
                _smoothMovementRequested = smooth, _showingTitleScreen = false };
            e._gameData.Tiles.Add(new Tile { Flags = TileFlags.Floor });
            e._gameData.Tiles.Add(new Tile { Flags = TileFlags.Object });
            e._gameData.Tiles.Add(new Tile { Flags = TileFlags.Object | TileFlags.Draggable });
            e._gameData.Tiles.Add(new Tile { Flags = TileFlags.Item });
            for (int id = 0; id < 2; id++)
            {
                var z = new Zone { Id = id, Width = 9, Height = 9, TileGrid = new ushort[9,9,3] };
                for (int y = 0; y < 9; y++) for (int x = 0; x < 9; x++)
                { z.SetTile(x,y,1,ushort.MaxValue); z.SetTile(x,y,2,ushort.MaxValue); }
                e._gameData.Zones.Add(z);
            }
            e._gameData.Zones[0].SetTile(3,2,1,2);
            e.LoadZone(0,2,2);
            return e;
        }
        static void Move(GameEngine e, int dx, Direction facing, bool pull = false)
        { e.TryMovePlayer(dx,0,facing,pull); if(e._smoothMovement)e.UpdateSmoothMovement(1.0/6); }
        foreach(bool smooth in new[]{false,true})
        {
            var e = Scene(smooth); var zone = e._state.CurrentZone!;
            Move(e,1,Direction.Right); Move(e,1,Direction.Right);
            zone.SetTile(7,7,0,1); e._state.SetCounter(0,8); e._state.DisabledActions.Add((0,3));
            e.LoadZone(999);
            Require(zone.GetTile(5,2,1)==2,"invalid transition reset a zone");
            e.LoadZone(0,1,1);
            Require(zone.GetTile(5,2,1)==2,"same-zone reload reset the area");
            e.LoadZone(1,1,1);
            Require(zone.GetTile(3,2,1)==2 && zone.GetTile(4,2,1)==ushort.MaxValue && zone.GetTile(5,2,1)==ushort.MaxValue,
                "unsolved area did not restore its initial layout");
            Require(zone.GetTile(7,7,0)==0 && e._state.GetCounter(0)==0 && !e._state.DisabledActions.Contains((0,3)),
                "local puzzle state was not reset");
            e.LoadZone(0,2,2); Move(e,1,Direction.Right); Move(e,-1,Direction.Left,true); Move(e,-1,Direction.Left,true);
            e.LoadZone(1,1,1);
            Require(zone.GetTile(3,2,1)==2 && zone.GetTile(2,2,1)==ushort.MaxValue,"pulling was not reset");

            e = Scene(smooth); zone=e._state.CurrentZone!;
            Move(e,1,Direction.Right); e._state.MarkZoneSolved(0); e._state.SetCounter(0,9);
            e.LoadZone(1,1,1);
            Require(zone.GetTile(4,2,1)==2 && zone.GetTile(3,2,1)==ushort.MaxValue && e._state.GetCounter(0)==9,
                "solved area was reset");
        }
        var engine=Scene(); var z0=engine._state.CurrentZone!;
        engine._worldGenerator=new WorldGenerator(engine._gameData!);
        var world=new WorldMap(); world.Sectors[0]=new Sector{Solved=true}; engine._worldGenerator.SetCurrentWorld(world);
        Move(engine,1,Direction.Right); engine.LoadZone(1,1,1);
        Require(z0.GetTile(4,2,1)==2,"sector puzzle solved status was ignored");

        engine=Scene(); z0=engine._state.CurrentZone!;
        // Capture an initial collectible and hotspot, then collect it before leaving.
        engine._initialAreas.Clear(); z0.SetTile(7,7,1,3);
        z0.Objects.Add(new ZoneObject { X=7,Y=7,Argument=3,Type=ZoneObjectType.CrateItem,Enabled=true });
        engine.CaptureInitialArea(z0); Move(engine,1,Direction.Right);
        z0.SetTile(7,7,1,ushort.MaxValue); engine._state.AddItem(3); engine._state.MarkObjectCollected(0,7,7);
        engine.LoadZone(1,1,1); engine.LoadZone(0,2,2);
        Require(z0.GetTile(7,7,1)==ushort.MaxValue && engine._state.Inventory.Contains(3),"reset recreated a collected reward");

        engine=Scene(); z0=engine._state.CurrentZone!;
        engine._initialAreas.Clear();
        z0.Actions.Add(new Data.Action { Instructions={new Instruction{Opcode=InstructionOpcode.AddItem,Arguments={3}},new Instruction{Opcode=InstructionOpcode.DisableAction}} });
        z0.Actions.Add(new Data.Action { Instructions={new Instruction{Opcode=InstructionOpcode.SetCounter,Arguments={5}},new Instruction{Opcode=InstructionOpcode.DisableAction}} });
        engine.CaptureInitialArea(z0); Move(engine,1,Direction.Right);
        engine._state.DisabledActions.Add((0,0)); engine._state.DisabledActions.Add((0,1)); engine.LoadZone(1,1,1);
        Require(engine._state.DisabledActions.Contains((0,0)) && !engine._state.DisabledActions.Contains((0,1)),
            "reset either replayed a reward handoff or left puzzle actions disabled");
    }
}
