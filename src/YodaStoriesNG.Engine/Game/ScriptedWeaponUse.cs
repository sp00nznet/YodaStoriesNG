using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Native ranged-weapon reach for authored item-placement targets.</summary>
public static class ScriptedWeaponUse
{
    public static int Range(GameData data,int item) => data.GameType==GameType.YodaStories &&
        item>=0 && item<data.Tiles.Count && data.Tiles[item].IsWeapon &&
        (data.Tiles[item].Flags & (TileFlags.WeaponTheForce|TileFlags.WeaponLightBlaster|TileFlags.WeaponHeavyBlaster))!=0 ? 3 : 0;

    // The native weapon animation treats these low terrain pictures as permeable.
    // Object flags describe walking collision and cannot alone stop a beam.
    public static bool Permeable(GameData data,int tile) => tile==ushort.MaxValue || tile>=0 && tile<data.Tiles.Count &&
        (!data.Tiles[tile].IsObject || data.GameType==GameType.YodaStories && (tile is
            >=0x67 and <=0x69 or 0x6b or 0x163 or >=0x176 and <=0x178 or
            >=0x17c and <=0x186 or >=0x271 and <=0x272 or >=0x346 and <=0x357 or
            >=0x396 and <=0x398 or 0x39b or >=0x39d and <=0x3a4 or 0x3fd or
            >=0x47b and <=0x486 or 0x53d or 0x740));

    public static bool CanAim(GameData data,Zone zone,int item,int x,int y,int tx,int ty)
    {
        int dx=Math.Sign(tx-x),dy=Math.Sign(ty-y),distance=Math.Abs(tx-x)+Math.Abs(ty-y);
        if(dx!=0 && dy!=0 || distance<1 || distance>Range(data,item)) return false;
        for(int step=1;step<distance;step++)
            if(!Permeable(data,zone.GetTile(x+dx*step,y+dy*step,1))) return false;
        return true;
    }
}

public unsafe partial class GameEngine
{
    private void ApplyScriptedWeaponStrike(int item)
    {
        if(_state.CurrentZone is not { } zone || _actionExecutor==null) return;
        var (dx,dy)=_state.PlayerDirection switch {
            Direction.Up=>(0,-1),Direction.Down=>(0,1),Direction.Left=>(-1,0),_=>(1,0) };
        for(int distance=1;distance<=ScriptedWeaponUse.Range(_gameData!,item);distance++)
        {
            int x=_state.PlayerX+dx*distance,y=_state.PlayerY+dy*distance;
            if(x<0 || y<0 || x>=zone.Width || y>=zone.Height) break;
            if(_actionExecutor.WouldHandleItemPlacement(item,x,y,weapon:true))
            {
                _actionExecutor.ExecuteWeaponPlacement(item,x,y);
                // A cutscene or script owns subsequent frames once it starts.
                break;
            }
            if(!ScriptedWeaponUse.Permeable(_gameData!,zone.GetTile(x,y,1))) break;
        }
    }
}
