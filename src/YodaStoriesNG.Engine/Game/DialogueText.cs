using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Expand native item-name tokens before text reaches the bitmap font.</summary>
internal static class DialogueText
{
    public static string Resolve(GameData data, string text, int providedItem, int requiredItem)
    {
        string Name(int item) => item >= 0 && data.TileNames.TryGetValue(item, out var name) &&
            !string.IsNullOrWhiteSpace(name) ? name.Trim() : "item";

        // Native Latin-1 tokens, plus the equivalent ISO-8859-2 Unicode forms.
        string provided = Name(providedItem), required = Name(requiredItem);
        return text.Replace("¢", provided).Replace("Ľ", provided)
            .Replace("¥", required).Replace("˘", required);
    }
}
