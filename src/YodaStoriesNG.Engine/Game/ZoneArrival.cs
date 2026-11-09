using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Actor occupancy at the positions restored when an area is loaded.</summary>
public static class ZoneArrival
{
    public static bool SpawnWalkable(GameData data,Zone zone,int x,int y,Func<Zone,int,int,bool>? canWalk=null)
    {
        if(x<0 || y<0 || x>=zone.Width || y>=zone.Height ||
            TileCollision.IsGroundBlocked(data,zone,x,y) || canWalk?.Invoke(zone,x,y)==false) return false;
        int tile=zone.GetTile(x,y,1);
        return tile>=data.Tiles.Count || !data.Tiles[tile].IsObject ||
            data.Tiles[tile].IsDraggable;
    }

    public static (int X,int Y)? NearestSpawn(GameData data,Zone zone,int x,int y,Func<Zone,int,int,bool>? canWalk=null)
    {
        for(int radius=1;radius<Math.Max(zone.Width,zone.Height);radius++)
        for(int dy=-radius;dy<=radius;dy++) for(int dx=-radius;dx<=radius;dx++)
            if((Math.Abs(dx)==radius || Math.Abs(dy)==radius) && SpawnWalkable(data,zone,x+dx,y+dy,canWalk))
                return (x+dx,y+dy);
        if(SpawnWalkable(data,zone,zone.Width/2,zone.Height/2,canWalk)) return (zone.Width/2,zone.Height/2);
        for(int row=1;row<zone.Height-1;row++) for(int column=1;column<zone.Width-1;column++)
            if(SpawnWalkable(data,zone,column,row,canWalk)) return (column,row);
        return null;
    }

    public static (int X,int Y) ResolveSpawn(GameData data,Zone zone,int x,int y,bool byPlane,
        Func<Zone,int,int,bool>? canWalk=null) => byPlane || SpawnWalkable(data,zone,x,y,canWalk)
            ? (x,y) : NearestSpawn(data,zone,x,y,canWalk) ?? (x,y);

    // Indy also uses the arrival phase for outdoor NPC introductions. Transport
    // zones use it for disembarking instead, so ordinary boundary entry there
    // must retain its edge landing.
    public static bool IsOutdoorIntroduction(GameType game,Data.Action action) =>
        game==GameType.IndianaJones && action.Conditions.Any(c=>c.Opcode==ConditionOpcode.EnterByPlane) &&
        !action.Instructions.Any(i=>i.Opcode is InstructionOpcode.MoveHeroTo or InstructionOpcode.MoveHeroBy or
            InstructionOpcode.ChangeZone or InstructionOpcode.HideHero);

    public static bool HasLivingActor(Zone zone, int x, int y, GameState state) =>
        zone.Monsters.Select((monster, index) => (monster, index)).Any(p =>
            p.monster.CharacterId != ushort.MaxValue && p.monster.X == x && p.monster.Y == y &&
            !state.DefeatedMonsters.Contains($"{zone.Id}_{p.index}") &&
            !state.DisabledMonsters.Contains($"{zone.Id}_{p.index}"));
}
