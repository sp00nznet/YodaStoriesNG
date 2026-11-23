using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Authored doorway landings that open their own room entrance.</summary>
public static class DoorwayArrival
{
    public static (int X,int Y)[] ExitOffsets(Zone zone,int y) => y>=zone.Height-1
        ? new[] { (0,-1),(1,0),(-1,0),(0,1) }
        : new[] { (0,1),(1,0),(-1,0),(0,-1) };

    public static bool HasMovableExitCover(GameData data,Zone zone,int x,int y)
    {
        int tile=zone.GetTile(x,y+(y>=zone.Height-1?-1:1),1);
        return tile<data.Tiles.Count && data.Tiles[tile].IsDraggable;
    }

    public static bool HasEntryTrigger(Zone zone,int x,int y,GameState state) =>
        zone.Actions.Select((a,i)=>(a,i)).Any(p=>!state.DisabledActions.Contains((zone.Id,p.i)) &&
            p.a.Conditions.Count>0 && p.a.Conditions.All(c=>c.Opcode==ConditionOpcode.StandingOn &&
                c.Arguments.Count>=3 && c.Arguments[0]==x && c.Arguments[1]==y &&
                zone.GetTile(x,y,0)==unchecked((ushort)c.Arguments[2])) &&
            p.a.Instructions.Any(i=>i.Opcode is InstructionOpcode.EnableMonster or InstructionOpcode.EnableAllMonsters ||
                i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                i.Arguments[2]==1 && Math.Abs(i.Arguments[0]-x)+Math.Abs(i.Arguments[1]-y)==1));
}
