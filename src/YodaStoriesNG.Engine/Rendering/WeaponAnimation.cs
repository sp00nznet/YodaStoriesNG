using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Rendering;

internal readonly record struct WeaponPose(ushort Body, ushort? Extension, int OffsetX, int OffsetY);

/// <summary>Weapon CHAR frame zero is the projectile/icon; frames one and two are the hero's attack poses.</summary>
internal static class WeaponAnimation
{
    public static int FindCharacter(GameData data, int inventoryTile)
    {
        return data.Characters.FindIndex(c => c.Weapon != null &&
            c.Frames.ExtensionRight.FirstOrDefault(ushort.MaxValue) == inventoryTile);
    }

    public static WeaponPose? Resolve(GameData data, int characterIndex, Direction direction, double progress)
    {
        if (characterIndex < 0 || characterIndex >= data.Characters.Count) return null;
        var frames = data.Characters[characterIndex].Frames;
        var (body, extension, dx, dy) = direction switch
        {
            Direction.Up => (frames.WalkUp, frames.ExtensionUp, 0, -1),
            Direction.Down => (frames.WalkDown, frames.ExtensionDown, 0, 1),
            Direction.Left => (frames.WalkLeft, frames.ExtensionLeft, -1, 0),
            _ => (frames.WalkRight, frames.ExtensionRight, 1, 0)
        };
        int preferred = progress < 0.5 ? 1 : 2;
        foreach (int index in new[] { preferred, preferred == 1 ? 2 : 1 })
        {
            // Never substitute frame zero: it can be a blaster bolt or an inventory icon.
            if (index >= body.Length || !Valid(body[index])) continue;
            ushort? extra = index < extension.Length && Valid(extension[index]) ? extension[index] : null;
            return new WeaponPose(body[index], extra, dx, dy);
        }
        return null;

        bool Valid(ushort tile) => tile > 0 && tile < data.Tiles.Count;
    }
}
