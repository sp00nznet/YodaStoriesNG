using System.Text.Json;
using Hexa.NET.SDL2;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Manages customizable keyboard bindings for game actions.
/// </summary>
public class KeyBindings
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YodaStoriesNG", "keybindings.json");

    // Action names (used as keys in the dictionary)
    public const string MoveUp = "MoveUp";
    public const string MoveDown = "MoveDown";
    public const string MoveLeft = "MoveLeft";
    public const string MoveRight = "MoveRight";
    public const string Action = "Action";
    public const string ToggleWeapon = "ToggleWeapon";
    public const string Travel = "Travel";
    public const string Objective = "Objective";
    public const string Restart = "Restart";
    public const string ToggleSound = "ToggleSound";
    public const string Quit = "Quit";
    public const string DebugOverlay = "DebugOverlay";
    public const string MapViewer = "MapViewer";
    public const string ScriptEditor = "ScriptEditor";
    public const string AssetViewer = "AssetViewer";
    public const string NextZone = "NextZone";
    public const string PrevZone = "PrevZone";
    public const string FindZone = "FindZone";
    public const string Inspect = "Inspect";
    public const string ToggleBot = "ToggleBot";
    public const string Inventory1 = "Inventory1";
    public const string Inventory2 = "Inventory2";
    public const string Inventory3 = "Inventory3";
    public const string Inventory4 = "Inventory4";
    public const string Inventory5 = "Inventory5";
    public const string Inventory6 = "Inventory6";
    public const string Inventory7 = "Inventory7";
    public const string Inventory8 = "Inventory8";

    // Primary and alternate key bindings
    public Dictionary<string, int> Primary { get; set; } = new();
    public Dictionary<string, int> Alternate { get; set; } = new();

    // Display names for the UI
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        { MoveUp, "Move Up" },
        { MoveDown, "Move Down" },
        { MoveLeft, "Move Left" },
        { MoveRight, "Move Right" },
        { Action, "Action / Attack / Talk" },
        { ToggleWeapon, "Toggle Weapon" },
        { Travel, "Travel (X-Wing)" },
        { Objective, "Show Objective" },
        { Restart, "Restart Game" },
        { ToggleSound, "Toggle Sound" },
        { Quit, "Quit" },
        { DebugOverlay, "Debug Overlay" },
        { MapViewer, "Map Viewer" },
        { ScriptEditor, "Script Editor" },
        { AssetViewer, "Asset Viewer" },
        { NextZone, "Next Zone" },
        { PrevZone, "Previous Zone" },
        { FindZone, "Find Zone" },
        { Inspect, "Inspect" },
        { ToggleBot, "Toggle Bot" },
        { Inventory1, "Inventory Slot 1" },
        { Inventory2, "Inventory Slot 2" },
        { Inventory3, "Inventory Slot 3" },
        { Inventory4, "Inventory Slot 4" },
        { Inventory5, "Inventory Slot 5" },
        { Inventory6, "Inventory Slot 6" },
        { Inventory7, "Inventory Slot 7" },
        { Inventory8, "Inventory Slot 8" },
    };

    // Categories for grouping in UI
    public static readonly (string category, string[] actions)[] Categories = new[]
    {
        ("Movement", new[] { MoveUp, MoveDown, MoveLeft, MoveRight }),
        ("Actions", new[] { Action, ToggleWeapon, Travel, Objective }),
        ("Inventory", new[] { Inventory1, Inventory2, Inventory3, Inventory4, Inventory5, Inventory6, Inventory7, Inventory8 }),
        ("Game", new[] { Restart, ToggleSound, Quit }),
        ("Debug", new[] { DebugOverlay, MapViewer, ScriptEditor, AssetViewer, NextZone, PrevZone, FindZone, Inspect, ToggleBot }),
    };

    public KeyBindings()
    {
        SetDefaults();
    }

    public void SetDefaults()
    {
        // Primary bindings (arrow keys / standard)
        Primary = new Dictionary<string, int>
        {
            { MoveUp, (int)SDLKeyCode.Up },
            { MoveDown, (int)SDLKeyCode.Down },
            { MoveLeft, (int)SDLKeyCode.Left },
            { MoveRight, (int)SDLKeyCode.Right },
            { Action, (int)SDLKeyCode.Space },
            { ToggleWeapon, (int)SDLKeyCode.Tab },
            { Travel, (int)SDLKeyCode.X },
            { Objective, (int)SDLKeyCode.O },
            { Restart, (int)SDLKeyCode.R },
            { ToggleSound, (int)SDLKeyCode.M },
            { Quit, (int)SDLKeyCode.Escape },
            { DebugOverlay, (int)SDLKeyCode.F1 },
            { MapViewer, (int)SDLKeyCode.F2 },
            { ScriptEditor, (int)SDLKeyCode.F3 },
            { AssetViewer, (int)SDLKeyCode.F4 },
            { NextZone, (int)SDLKeyCode.N },
            { PrevZone, (int)SDLKeyCode.P },
            { FindZone, (int)SDLKeyCode.F },
            { Inspect, (int)SDLKeyCode.I },
            { ToggleBot, (int)SDLKeyCode.B },
            { Inventory1, (int)SDLKeyCode.K1 },
            { Inventory2, (int)SDLKeyCode.K2 },
            { Inventory3, (int)SDLKeyCode.K3 },
            { Inventory4, (int)SDLKeyCode.K4 },
            { Inventory5, (int)SDLKeyCode.K5 },
            { Inventory6, (int)SDLKeyCode.K6 },
            { Inventory7, (int)SDLKeyCode.K7 },
            { Inventory8, (int)SDLKeyCode.K8 },
        };

        // Alternate bindings (WASD)
        Alternate = new Dictionary<string, int>
        {
            { MoveUp, (int)SDLKeyCode.W },
            { MoveDown, (int)SDLKeyCode.S },
            { MoveLeft, (int)SDLKeyCode.A },
            { MoveRight, (int)SDLKeyCode.D },
        };
    }

    /// <summary>
    /// Checks if a key matches the binding for an action.
    /// </summary>
    public bool IsPressed(string action, int keyCode)
    {
        if (Primary.TryGetValue(action, out var primary) && primary == keyCode)
            return true;
        if (Alternate.TryGetValue(action, out var alternate) && alternate == keyCode)
            return true;
        return false;
    }

    /// <summary>
    /// Sets the primary binding for an action.
    /// </summary>
    public void SetPrimary(string action, int keyCode)
    {
        Primary[action] = keyCode;
    }

    /// <summary>
    /// Sets the alternate binding for an action.
    /// </summary>
    public void SetAlternate(string action, int keyCode)
    {
        Alternate[action] = keyCode;
    }

    /// <summary>
    /// Clears the alternate binding for an action.
    /// </summary>
    public void ClearAlternate(string action)
    {
        Alternate.Remove(action);
    }

    /// <summary>
    /// Gets a display string for the current binding.
    /// </summary>
    public string GetBindingDisplay(string action)
    {
        var parts = new List<string>();

        if (Primary.TryGetValue(action, out var primary))
            parts.Add(GetKeyName(primary));

        if (Alternate.TryGetValue(action, out var alternate))
            parts.Add(GetKeyName(alternate));

        return parts.Count > 0 ? string.Join(" / ", parts) : "(unbound)";
    }

    /// <summary>
    /// Gets the display name for an SDL key code.
    /// </summary>
    public static string GetKeyName(int keyCode)
    {
        return keyCode switch
        {
            (int)SDLKeyCode.Up => "Up",
            (int)SDLKeyCode.Down => "Down",
            (int)SDLKeyCode.Left => "Left",
            (int)SDLKeyCode.Right => "Right",
            (int)SDLKeyCode.Space => "Space",
            (int)SDLKeyCode.Tab => "Tab",
            (int)SDLKeyCode.Escape => "Escape",
            (int)SDLKeyCode.Return => "Enter",
            (int)SDLKeyCode.Backspace => "Backspace",
            (int)SDLKeyCode.Delete => "Delete",
            (int)SDLKeyCode.Insert => "Insert",
            (int)SDLKeyCode.Home => "Home",
            (int)SDLKeyCode.End => "End",
            (int)SDLKeyCode.Pageup => "PageUp",
            (int)SDLKeyCode.Pagedown => "PageDown",
            (int)SDLKeyCode.F1 => "F1",
            (int)SDLKeyCode.F2 => "F2",
            (int)SDLKeyCode.F3 => "F3",
            (int)SDLKeyCode.F4 => "F4",
            (int)SDLKeyCode.F5 => "F5",
            (int)SDLKeyCode.F6 => "F6",
            (int)SDLKeyCode.F7 => "F7",
            (int)SDLKeyCode.F8 => "F8",
            (int)SDLKeyCode.F9 => "F9",
            (int)SDLKeyCode.F10 => "F10",
            (int)SDLKeyCode.F11 => "F11",
            (int)SDLKeyCode.F12 => "F12",
            (int)SDLKeyCode.K0 => "0",
            (int)SDLKeyCode.K1 => "1",
            (int)SDLKeyCode.K2 => "2",
            (int)SDLKeyCode.K3 => "3",
            (int)SDLKeyCode.K4 => "4",
            (int)SDLKeyCode.K5 => "5",
            (int)SDLKeyCode.K6 => "6",
            (int)SDLKeyCode.K7 => "7",
            (int)SDLKeyCode.K8 => "8",
            (int)SDLKeyCode.K9 => "9",
            (int)SDLKeyCode.Lshift => "LShift",
            (int)SDLKeyCode.Rshift => "RShift",
            (int)SDLKeyCode.Lctrl => "LCtrl",
            (int)SDLKeyCode.Rctrl => "RCtrl",
            (int)SDLKeyCode.Lalt => "LAlt",
            (int)SDLKeyCode.Ralt => "RAlt",
            _ => ((char)keyCode >= 'a' && (char)keyCode <= 'z') ? ((char)keyCode).ToString().ToUpper() : $"Key{keyCode}"
        };
    }

    /// <summary>
    /// Saves bindings to the config file.
    /// </summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigPath, json);
            Console.WriteLine($"[KeyBindings] Saved to {ConfigPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KeyBindings] Failed to save: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads bindings from the config file.
    /// </summary>
    public static KeyBindings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var bindings = JsonSerializer.Deserialize<KeyBindings>(json);
                if (bindings != null)
                {
                    Console.WriteLine($"[KeyBindings] Loaded from {ConfigPath}");
                    return bindings;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KeyBindings] Failed to load: {ex.Message}");
        }

        Console.WriteLine("[KeyBindings] Using defaults");
        return new KeyBindings();
    }
}
