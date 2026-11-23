using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    public static void TestScriptedWeaponUse()
    {
        static void Require(bool ok,string text) { if(!ok) throw new InvalidOperationException(text); }
        var engine=new GameEngine("") { _gameData=new GameData { GameType=GameType.YodaStories } };
        for(int i=0;i<=511;i++) engine._gameData.Tiles.Add(new Tile { Id=i,Flags=TileFlags.Floor });
        engine._gameData.Tiles[511].Flags=TileFlags.Weapon|TileFlags.WeaponTheForce;
        engine._gameData.Tiles[381].Flags=TileFlags.Object;
        engine._gameData.Tiles[1].Flags=TileFlags.Object;
        var zone=new Zone { Id=0,Width=8,Height=8,TileGrid=new ushort[8,8,3] };
        for(int y=0;y<8;y++) for(int x=0;x<8;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.SetTile(5,4,1,381);
        engine._state.CurrentZone=zone; engine._state.PlayerX=6; engine._state.PlayerY=4;
        engine._state.PlayerDirection=Direction.Left;
        engine._actionExecutor=new ActionExecutor(engine._gameData,engine._state);
        zone.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.PlacedItemIs,Arguments={3,4,0,-1,511} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={1} },
            new Instruction { Opcode=InstructionOpcode.Wait,Arguments={0} },
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={2} } } });
        zone.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.PlacedItemIsNot,Arguments={3,4,0,0,510} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={99} } } });
        Require(ScriptedWeaponUse.CanAim(engine._gameData,zone,511,6,4,3,4),"low terrain blocked native weapon reach");
        Require(!ScriptedWeaponUse.CanAim(engine._gameData,zone,511,6,4,2,4),"weapon target exceeded native range");
        Require(!ScriptedWeaponUse.CanAim(engine._gameData,zone,511,6,4,3,3),"weapon targeting accepted a diagonal ray");
        engine.ApplyScriptedWeaponStrike(511);
        Require(engine._state.GetCounter(0)==1 && engine._actionExecutor.IsBusy,"weapon did not activate wildcard target");
        engine._actionExecutor.ExecuteZoneActions(ActionTrigger.Walk);
        Require(engine._state.GetCounter(0)==2,"weapon context was lost across Wait or triggered wrong-item dialogue");
        engine._state.SetCounter(0,0); zone.SetTile(5,4,1,1);
        engine.ApplyScriptedWeaponStrike(511);
        Require(engine._state.GetCounter(0)==0,"weapon activated a target through opaque terrain");
    }
}
