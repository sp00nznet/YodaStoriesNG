using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// The docs capture harness: a fixed timeline of "at T seconds, do this" steps that
/// walks the game through every screen the README shows and asks
/// <see cref="Dev.Capture"/> to save each one. Runs only when YSNG_CAPTURE_DIR is set,
/// and quits the game when the timeline ends.
///
/// It lives in GameEngine (as a partial) because every interesting screen is reached
/// through private engine state - there is no scripting seam worth inventing for it.
/// See docs/CAPTURING-SCREENSHOTS.md for the full pipeline.
/// </summary>
public unsafe partial class GameEngine
{
    private List<(double at, System.Action step)>? _captureSteps;
    private int _captureNext;
    private double _captureTime;

    // The title-screen flyby crosses x = -100 -> 900 at 150 px/s, so one full pass is
    // 6.7s. 90 frames at 75ms covers it, and becomes docs/hero.gif at ~13fps.
    private const int HeroFrames = 90;
    private const double HeroFrameGap = 0.075;
    private const double HeroStart = 0.4;

    // Palette cycling runs on a 150ms timer; sampling at 75ms shows every step.
    private const int PaletteFrames = 24;
    private const double PaletteFrameGap = 0.075;

    private void CaptureTick(double deltaTime)
    {
        _captureSteps ??= BuildCaptureTimeline();
        _captureTime += deltaTime;

        while (_captureNext < _captureSteps.Count && _captureTime >= _captureSteps[_captureNext].at)
        {
            _captureSteps[_captureNext].step();
            _captureNext++;
        }
    }

    private List<(double, System.Action)> BuildCaptureTimeline()
    {
        // File prefix so a Yoda run and an Indy run can share one output directory.
        string game = _gameData?.GameType == GameType.IndianaJones ? "indy" : "yoda";
        var t = new List<(double, System.Action)>();

        Console.WriteLine($"[capture] recording '{game}' shots to {Dev.Capture.Dir}");

        // --- Title screen: the X-Wing (Yoda) / vehicle flyby, frame by frame.
        for (int i = 0; i < HeroFrames; i++)
        {
            int frame = i;
            t.Add((HeroStart + frame * HeroFrameGap,
                () => Dev.Capture.Request("game", $"{game}-hero/f{frame:D3}")));
        }
        double after = HeroStart + HeroFrames * HeroFrameGap;

        // --- Gameplay: let the bot play so the shot has NPCs, items and a real HUD.
        t.Add((after + 0.5, () => { _showingTitleScreen = false; _titleScreen?.Hide(); StartNewGame(); }));
        t.Add((after + 1.0, EnableBot));
        // Indiana Jones worlds do not generate a mission chain yet, so its bot leaves the
        // hero standing in an empty field. Yoda's bot finds its own way somewhere worth
        // photographing, so only Indy needs the nudge.
        if (game == "indy")
            t.Add((after + 16.0, FindZoneWithContent));

        // Park the bot and let its toast messages ("Can't go that way", zone banners)
        // expire before the shutter, so the frame shows the game and not the harness.
        t.Add((after + 17.0, () => { DisableBot(); _messages.Clear(); }));
        t.Add((after + 18.0, () => Dev.Capture.Request("game", $"{game}-gameplay")));

        // --- Palette animation: park in the zone that uses the most cycling colours.
        double pal = after + 19.0;
        t.Add((pal, () => { LoadZone(FindMostAnimatedZone()); _messages.Clear(); }));
        for (int i = 0; i < PaletteFrames; i++)
        {
            int frame = i;
            t.Add((pal + 0.5 + frame * PaletteFrameGap,
                () => Dev.Capture.Request("game", $"{game}-palette/f{frame:D3}")));
        }

        // --- Tool windows. Each opens on whatever zone the player is in, so move to a
        //     zone with real scripted dialogue first - it is what the editor shot shows.
        double tools = pal + 0.5 + PaletteFrames * PaletteFrameGap + 1.0;
        t.Add((tools - 0.5, () => LoadZone(FindRichestScriptZone())));
        foreach (var (open, tag) in new (System.Action, string)[]
        {
            (() => _debugMapWindow?.Toggle(), "map"),
            (() => _scriptViewer?.Toggle(), "script-editor"),
            (() => _assetViewer?.Toggle(), "assets"),
            (() => _saveInspector?.Toggle(), "save-editor"),
        })
        {
            var openWindow = open;
            var windowTag = tag;
            t.Add((tools, openWindow));
            t.Add((tools + 1.0, () => Dev.Capture.Request(windowTag, $"{game}-{windowTag}")));
            t.Add((tools + 1.5, openWindow)); // Toggle is symmetric - this closes it again
            tools += 2.0;
        }

        // --- R2-D2 hint dialogue, overlaid on the live game view.
        t.Add((tools, () => { _state.HasLocator = true; ShowLocatorHint(); }));
        t.Add((tools + 0.4, () => Dev.Capture.Request("game", $"{game}-r2d2-help")));

        // --- End-game score. Faked to a plausible finished run rather than played out.
        t.Add((tools + 1.5, StageFinishedGame));
        t.Add((tools + 2.5, () => Dev.Capture.Request("score", $"{game}-score")));

        t.Add((tools + 3.5, () =>
        {
            Console.WriteLine("[capture] done");
            _isRunning = false;
        }));

        return t;
    }

    /// <summary>
    /// Picks the zone whose tiles use the most colour-cycled palette indices - i.e. the
    /// one with the most water/lava/fire on screen, which is what the animation shot needs.
    /// </summary>
    private int FindMostAnimatedZone()
    {
        if (_gameData == null || _gameData.Zones.Count == 0) return 0;

        // Cost of scanning a tile's 1024 pixels adds up, so score each tile once.
        var tileScore = new int[_gameData.Tiles.Count];
        for (int id = 0; id < _gameData.Tiles.Count; id++)
        {
            int animated = 0;
            foreach (var px in _gameData.Tiles[id].PixelData)
                if (Palette.IsAnimatedIndex(px)) animated++;
            tileScore[id] = animated;
        }

        int bestZone = 0, bestScore = -1;
        foreach (var zone in _gameData.Zones)
        {
            if (zone.TileGrid == null) continue;
            int score = 0;
            for (int layer = 0; layer < 3; layer++)
                for (int y = 0; y < zone.Height; y++)
                    for (int x = 0; x < zone.Width; x++)
                    {
                        ushort id = zone.GetTile(x, y, layer);
                        if (id < tileScore.Length) score += tileScore[id];
                    }

            if (score > bestScore)
            {
                bestScore = score;
                bestZone = zone.Id;
            }
        }

        Console.WriteLine($"[capture] most animated zone: {bestZone} (score {bestScore})");
        return bestZone;
    }

    /// <summary>
    /// Picks the zone with the most readable scripted dialogue, so the script editor shot
    /// shows conditions and speech rather than an empty or single-opcode zone.
    /// </summary>
    private int FindRichestScriptZone()
    {
        if (_gameData == null) return _state.CurrentZoneId;

        int bestZone = _state.CurrentZoneId, bestScore = 0;
        foreach (var zone in _gameData.Zones)
        {
            int score = 0;
            foreach (var action in zone.Actions)
            {
                score += action.Conditions.Count;
                foreach (var instruction in action.Instructions)
                {
                    var text = instruction.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    // Only count text that reads as English - garbled runs of section
                    // tags and control bytes should not win the shot.
                    int letters = text.Count(c => char.IsLetter(c) || c == ' ');
                    if (letters > text.Length * 0.9) score += letters;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestZone = zone.Id;
            }
        }

        Console.WriteLine($"[capture] richest script zone: {bestZone} (score {bestScore})");
        return bestZone;
    }

    /// <summary>Puts the state into a "finished a good run" shape so the score screen shows real numbers.</summary>
    private void StageFinishedGame()
    {
        _state.GamesWon = 15;
        _state.TotalSectors = 12;
        _state.GameStartTime = DateTime.Now - TimeSpan.FromMinutes(14);
        for (int i = 0; i < 12; i++) _state.MarkZoneSolved(i);
        for (int i = 0; i < 17; i++) _state.MarkZoneVisited(i);
        _scoreWindow?.Show(_state, _gameData?.GameType ?? GameType.YodaStories);
    }
}
