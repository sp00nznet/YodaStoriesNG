using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Shared targeting rules for inventory use and authored whip hooks.</summary>
public static class ItemUseReach
{
    public static bool Extended(GameData data,int item) => data.GameType==GameType.IndianaJones && item==449;

    public static bool Contains(GameData data,int item,int fromX,int fromY,int targetX,int targetY)
    {
        int dx=Math.Abs(fromX-targetX),dy=Math.Abs(fromY-targetY);
        return Math.Max(dx,dy)<=1 || Extended(data,item) && (dx==0 || dy==0) && dx+dy==2;
    }
}
