using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Main game engine that coordinates all systems.
/// </summary>
public class GameEngine : IDisposable
{
    private GameData? _gameData;
    private GameState _state;
    private GameRenderer? _renderer;
    private ActionExecutor? _actionExecutor;

    private bool _isRunning;
    private readonly string _dataPath;

    // Timing
    private const double TargetFrameTime = 1.0 / 60.0; // 60 FPS
    private const double AnimationFrameTime = 0.15; // 150ms per animation frame

    public GameEngine(string dataPath)
    {
        _dataPath = dataPath;
        _state = new GameState();
    }

    /// <summary>
    /// Loads game data and initializes the engine.
    /// </summary>
    public bool Initialize()
    {
        Console.WriteLine("Loading game data...");

        // Parse the DTA file
        var dtaPath = Path.Combine(_dataPath, "yodesk.dta");
        if (!File.Exists(dtaPath))
        {
            Console.WriteLine($"Error: Could not find {dtaPath}");
            return false;
        }

        var parser = new DtaParser();
        _gameData = parser.Parse(dtaPath);

        Console.WriteLine($"Game version: {_gameData.Version}");
        Console.WriteLine($"Loaded: {_gameData.Tiles.Count} tiles, {_gameData.Zones.Count} zones, {_gameData.Characters.Count} characters");

        // Initialize renderer
        _renderer = new GameRenderer(_gameData);
        if (!_renderer.Initialize("Yoda Stories NG"))
        {
            Console.WriteLine("Failed to initialize renderer");
            return false;
        }

        // Initialize action executor
        _actionExecutor = new ActionExecutor(_gameData, _state);

        // Start new game
        StartNewGame();

        return true;
    }

    /// <summary>
    /// Starts a new game.
    /// </summary>
    public void StartNewGame()
    {
        _state.Reset();

        // Find the starting zone (typically zone 0 or first non-empty zone)
        for (int i = 0; i < _gameData!.Zones.Count; i++)
        {
            var zone = _gameData.Zones[i];
            if (zone.Width > 0 && zone.Height > 0)
            {
                LoadZone(i);
                break;
            }
        }
    }

    /// <summary>
    /// Loads a zone by ID.
    /// </summary>
    public void LoadZone(int zoneId)
    {
        if (zoneId < 0 || zoneId >= _gameData!.Zones.Count)
        {
            Console.WriteLine($"Invalid zone ID: {zoneId}");
            return;
        }

        _state.CurrentZoneId = zoneId;
        _state.CurrentZone = _gameData.Zones[zoneId];

        Console.WriteLine($"Loaded zone {zoneId}: {_state.CurrentZone.Width}x{_state.CurrentZone.Height}, planet: {_state.CurrentZone.Planet}");

        // Debug: Print first few tile IDs
        Console.WriteLine("Sample tile IDs from zone grid:");
        for (int y = 0; y < Math.Min(3, _state.CurrentZone.Height); y++)
        {
            for (int x = 0; x < Math.Min(3, _state.CurrentZone.Width); x++)
            {
                var bg = _state.CurrentZone.GetTile(x, y, 0);
                var mid = _state.CurrentZone.GetTile(x, y, 1);
                var fg = _state.CurrentZone.GetTile(x, y, 2);
                Console.WriteLine($"  [{x},{y}]: bg={bg}, mid={mid}, fg={fg}");
            }
        }

        // Reset camera for zone
        UpdateCamera();

        // Execute zone entry actions
        _actionExecutor?.ExecuteZoneActions(ActionTrigger.ZoneEnter);

        // Mark zone as initialized
        _state.SetVariable(_state.CurrentZoneId + 1000, 1);
    }

    /// <summary>
    /// Main game loop.
    /// </summary>
    public void Run()
    {
        _isRunning = true;
        var lastTime = DateTime.UtcNow;

        while (_isRunning)
        {
            var currentTime = DateTime.UtcNow;
            var deltaTime = (currentTime - lastTime).TotalSeconds;
            lastTime = currentTime;

            // Process input
            ProcessInput();

            // Update game state
            Update(deltaTime);

            // Render
            Render();

            // Frame limiting
            var frameTime = (DateTime.UtcNow - currentTime).TotalSeconds;
            if (frameTime < TargetFrameTime)
            {
                var sleepMs = (int)((TargetFrameTime - frameTime) * 1000);
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
            }
        }
    }

    private void ProcessInput()
    {
        while (_renderer!.PollEvent(out var evt))
        {
            switch ((SDLEventType)evt.Type)
            {
                case SDLEventType.Quit:
                    _isRunning = false;
                    break;

                case SDLEventType.Keydown:
                    HandleKeyDown(evt.Key.Keysym.Sym);
                    break;
            }
        }
    }

    private void HandleKeyDown(int keyCode)
    {
        // SDL key codes
        const int SDLK_ESCAPE = 27;
        const int SDLK_SPACE = 32;
        const int SDLK_UP = 1073741906;
        const int SDLK_DOWN = 1073741905;
        const int SDLK_LEFT = 1073741904;
        const int SDLK_RIGHT = 1073741903;
        const int SDLK_1 = 49;
        const int SDLK_8 = 56;
        const int SDLK_a = 97;
        const int SDLK_d = 100;
        const int SDLK_n = 110;
        const int SDLK_p = 112;
        const int SDLK_r = 114;
        const int SDLK_s = 115;
        const int SDLK_w = 119;

        switch (keyCode)
        {
            case SDLK_ESCAPE:
                _isRunning = false;
                break;

            case SDLK_UP:
            case SDLK_w:
                TryMovePlayer(0, -1, Direction.Up);
                break;

            case SDLK_DOWN:
            case SDLK_s:
                TryMovePlayer(0, 1, Direction.Down);
                break;

            case SDLK_LEFT:
            case SDLK_a:
                TryMovePlayer(-1, 0, Direction.Left);
                break;

            case SDLK_RIGHT:
            case SDLK_d:
                TryMovePlayer(1, 0, Direction.Right);
                break;

            case SDLK_SPACE:
                // Use item or attack
                UseItem();
                break;

            case >= SDLK_1 and <= SDLK_8:
                // Select inventory item (keys 1-8)
                var slot = keyCode - SDLK_1;
                if (slot < _state.Inventory.Count)
                    _state.SelectedItem = _state.Inventory[slot];
                break;

            case SDLK_r:
                // Restart/new game
                StartNewGame();
                break;

            case SDLK_n:
                // Next zone (debug)
                LoadZone((_state.CurrentZoneId + 1) % _gameData!.Zones.Count);
                break;

            case SDLK_p:
                // Previous zone (debug)
                LoadZone((_state.CurrentZoneId - 1 + _gameData!.Zones.Count) % _gameData.Zones.Count);
                break;
        }
    }

    private void TryMovePlayer(int dx, int dy, Direction direction)
    {
        _state.PlayerDirection = direction;

        var newX = _state.PlayerX + dx;
        var newY = _state.PlayerY + dy;

        // Check bounds
        if (newX < 0 || newX >= _state.CurrentZone!.Width ||
            newY < 0 || newY >= _state.CurrentZone.Height)
        {
            // Try to transition to adjacent zone
            HandleZoneTransition(dx, dy);
            return;
        }

        // Check collision with middle layer tile
        var middleTile = _state.CurrentZone.GetTile(newX, newY, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            if (tile.IsObject && !tile.IsDraggable)
            {
                // Collision - trigger bump action
                _actionExecutor?.ExecuteZoneActions(ActionTrigger.Bump);
                return;
            }
        }

        // Move player
        _state.PlayerX = newX;
        _state.PlayerY = newY;

        // Update camera
        UpdateCamera();

        // Execute walk actions
        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);

        // Check for zone objects at new position
        CheckZoneObjects();
    }

    private void HandleZoneTransition(int dx, int dy)
    {
        // Check for door objects at player position
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            if ((obj.Type == ZoneObjectType.DoorEntrance || obj.Type == ZoneObjectType.DoorExit) &&
                obj.X == _state.PlayerX && obj.Y == _state.PlayerY)
            {
                if (obj.Argument > 0 && obj.Argument < _gameData!.Zones.Count)
                {
                    LoadZone(obj.Argument);
                    return;
                }
            }
        }

        // TODO: Handle world map transitions
    }

    private void CheckZoneObjects()
    {
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            if (obj.X != _state.PlayerX || obj.Y != _state.PlayerY)
                continue;

            switch (obj.Type)
            {
                case ZoneObjectType.CrateItem:
                    // Pick up item
                    if (obj.Argument > 0)
                    {
                        _state.AddItem(obj.Argument);
                        Console.WriteLine($"Picked up item: {obj.Argument}");
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    // Pick up weapon
                    if (obj.Argument > 0)
                    {
                        _state.SelectedWeapon = obj.Argument;
                        Console.WriteLine($"Picked up weapon: {obj.Argument}");
                    }
                    break;

                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                    if (obj.Argument > 0 && obj.Argument < _gameData!.Zones.Count)
                    {
                        LoadZone(obj.Argument);
                    }
                    break;

                case ZoneObjectType.Teleporter:
                    Console.WriteLine("Teleporter activated!");
                    break;

                case ZoneObjectType.Trigger:
                    _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);
                    break;
            }
        }
    }

    private void UseItem()
    {
        if (_state.SelectedItem.HasValue)
        {
            _actionExecutor?.ExecuteZoneActions(ActionTrigger.UseItem);
        }
        else if (_state.SelectedWeapon.HasValue)
        {
            _actionExecutor?.ExecuteZoneActions(ActionTrigger.Attack);
        }
    }

    private void UpdateCamera()
    {
        if (_state.CurrentZone == null)
            return;

        // Center camera on player for large zones
        var viewportTiles = GameRenderer.ViewportTilesX;
        var halfViewport = viewportTiles / 2;

        if (_state.CurrentZone.Width > viewportTiles)
        {
            _state.CameraX = Math.Clamp(
                _state.PlayerX - halfViewport,
                0,
                _state.CurrentZone.Width - viewportTiles);
        }
        else
        {
            _state.CameraX = 0;
        }

        if (_state.CurrentZone.Height > viewportTiles)
        {
            _state.CameraY = Math.Clamp(
                _state.PlayerY - halfViewport,
                0,
                _state.CurrentZone.Height - viewportTiles);
        }
        else
        {
            _state.CameraY = 0;
        }
    }

    private void Update(double deltaTime)
    {
        if (_state.IsPaused)
            return;

        // Update animation
        _state.AnimationTimer += deltaTime;
        if (_state.AnimationTimer >= AnimationFrameTime)
        {
            _state.AnimationTimer -= AnimationFrameTime;
            _state.AnimationFrame = (_state.AnimationFrame + 1) % 3;
        }

        // Check for game over conditions
        if (_state.Health <= 0 && !_state.IsGameOver)
        {
            _state.IsGameOver = true;
            Console.WriteLine("Game Over!");
        }
    }

    private void Render()
    {
        if (_renderer == null || _state.CurrentZone == null)
            return;

        // Render zone
        _renderer.RenderZone(_state.CurrentZone, _state.CameraX, _state.CameraY);

        // Render player character
        RenderPlayer();

        // Render HUD
        _renderer.RenderHUD(_state.Health, _state.MaxHealth, _state.Inventory, _state.SelectedWeapon);

        // Present frame
        _renderer.Present();
    }

    private void RenderPlayer()
    {
        // Find hero character for rendering
        Character? heroChar = null;
        foreach (var character in _gameData!.Characters)
        {
            if (character.Type == CharacterType.Hero)
            {
                heroChar = character;
                break;
            }
        }

        if (heroChar != null)
        {
            // Get animation frame based on direction
            var frames = _state.PlayerDirection switch
            {
                Direction.Up => heroChar.Frames.WalkUp,
                Direction.Down => heroChar.Frames.WalkDown,
                Direction.Left => heroChar.Frames.WalkLeft,
                Direction.Right => heroChar.Frames.WalkRight,
                _ => heroChar.Frames.WalkDown
            };

            if (frames.Length > 0)
            {
                var frameIndex = Math.Min(_state.AnimationFrame, frames.Length - 1);
                var tileId = frames[frameIndex];
                _renderer!.RenderSprite(tileId, _state.PlayerX, _state.PlayerY, _state.CameraX, _state.CameraY);
                return;
            }
        }

        // Fallback: render a placeholder
        // Use first character tile if available
        if (_gameData.Tiles.Count > 800)
        {
            _renderer!.RenderSprite(800, _state.PlayerX, _state.PlayerY, _state.CameraX, _state.CameraY);
        }
    }

    public void Dispose()
    {
        _renderer?.Dispose();
    }
}
