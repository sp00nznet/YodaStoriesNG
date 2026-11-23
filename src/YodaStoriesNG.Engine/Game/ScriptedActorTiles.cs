using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

// The native renderer stamps active entity pictures into the script tile layer.
// Keep actors separate for rendering/collision, but expose the same picture to
// tile predicates without leaving stale pictures behind when an actor moves.
internal static class ScriptedActorTiles
{
    internal static bool IsTradeTarget(Zone zone,int x,int y,int tile) =>
        zone.Actions.Any(a=>a.Conditions.Any(c=>
            c.Opcode is ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot &&
            c.Arguments.Count>=4 && c.Arguments[0]==x && c.Arguments[1]==y &&
            c.Arguments[2]==1 && c.Arguments[3]==tile));

    internal static ushort[] Frames(Character character,Direction direction) => direction switch
    {
        Direction.Up=>character.Frames.WalkUp,Direction.Down=>character.Frames.WalkDown,
        Direction.Left=>character.Frames.WalkLeft,_=>character.Frames.WalkRight
    };

    internal static ushort? Picture(GameData data,NPC actor)
    {
        if(actor.CharacterId<0 || actor.CharacterId>=data.Characters.Count) return null;
        var frames=Frames(data.Characters[actor.CharacterId],actor.Direction);
        if(frames.Length==0) return null;
        int start=Math.Clamp(actor.AnimationFrame,0,frames.Length-1);
        for(int offset=0;offset<frames.Length;offset++)
        {
            ushort tile=frames[(start+offset)%frames.Length];
            if(tile>0 && tile<data.Tiles.Count) return tile;
        }
        return null;
    }

    internal static ushort Read(GameData data,GameState state,Zone zone,int x,int y,int layer)
    {
        if(layer==1 && zone.Id==state.CurrentZoneId && state.ZoneNPCs.FirstOrDefault(n=>
            n.IsEnabled && n.IsAlive && n.X==x && n.Y==y) is { } actor && Picture(data,actor) is ushort picture)
            return picture;
        return zone.GetTile(x,y,layer);
    }
}
