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
    public void LoadZone(int zoneId, int? spawnX = null, int? spawnY = null)
    {
        if (zoneId < 0 || zoneId >= _gameData!.Zones.Count)
        {
            Console.WriteLine($"Invalid zone ID: {zoneId}");
            return;
        }

        var zone = _gameData.Zones[zoneId];
        if (zone.Width == 0 || zone.Height == 0)
        {
            Console.WriteLine($"Zone {zoneId} is empty, skipping");
            return;
        }

        _state.CurrentZoneId = zoneId;
        _state.CurrentZone = zone;

        // Find spawn location
        if (spawnX.HasValue && spawnY.HasValue)
        {
            _state.PlayerX = spawnX.Value;
            _state.PlayerY = spawnY.Value;
        }
        else
        {
            // Look for spawn point in zone objects
            bool foundSpawn = false;
            foreach (var obj in zone.Objects)
            {
                if (obj.Type == ZoneObjectType.SpawnLocation)
                {
                    _state.PlayerX = obj.X;
                    _state.PlayerY = obj.Y;
                    foundSpawn = true;
                    break;
                }
            }

            // Default to center if no spawn found
            if (!foundSpawn)
            {
                _state.PlayerX = zone.Width / 2;
                _state.PlayerY = zone.Height / 2;
            }
        }

        Console.WriteLine($"Zone {zoneId}: {zone.Width}x{zone.Height}, planet: {zone.Planet}, spawn: ({_state.PlayerX},{_state.PlayerY})");

        // Initialize NPCs from zone objects
        InitializeZoneNPCs();

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
                    // Pick up item (if not already collected)
                    if (obj.Argument > 0 && !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        _state.AddItem(obj.Argument);
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        Console.WriteLine($"Picked up item: {obj.Argument}");
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    // Pick up weapon (if not already collected)
                    if (obj.Argument > 0 && !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        _state.SelectedWeapon = obj.Argument;
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        Console.WriteLine($"Picked up weapon: {obj.Argument}");
                    }
                    break;

                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                    if (obj.Argument > 0 && obj.Argument < _gameData!.Zones.Count)
                    {
                        // Find spawn point in destination zone
                        var destZone = _gameData.Zones[obj.Argument];
                        int? spawnX = null, spawnY = null;

                        // Look for a door that leads back to current zone, or a spawn point
                        foreach (var destObj in destZone.Objects)
                        {
                            if ((destObj.Type == ZoneObjectType.DoorEntrance || destObj.Type == ZoneObjectType.DoorExit) &&
                                destObj.Argument == _state.CurrentZoneId)
                            {
                                spawnX = destObj.X;
                                spawnY = destObj.Y;
                                break;
                            }
                            if (destObj.Type == ZoneObjectType.SpawnLocation && !spawnX.HasValue)
                            {
                                spawnX = destObj.X;
                                spawnY = destObj.Y;
                            }
                        }

                        LoadZone(obj.Argument, spawnX, spawnY);
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
        else
        {
            // Attack in facing direction
            PerformAttack();
        }
    }

    private void PerformAttack()
    {
        // Calculate attack position based on facing direction
        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (_state.PlayerDirection)
        {
            case Direction.Up: targetY--; break;
            case Direction.Down: targetY++; break;
            case Direction.Left: targetX--; break;
            case Direction.Right: targetX++; break;
        }

        // Check for NPC at target position
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            if (npc.X == targetX && npc.Y == targetY)
            {
                // Calculate damage (base damage + weapon bonus)
                int damage = 25;
                if (_state.SelectedWeapon.HasValue)
                {
                    damage = 50;  // Weapon does more damage
                }

                bool killed = npc.TakeDamage(damage);
                Console.WriteLine($"Player attacks! NPC takes {damage} damage. NPC Health: {npc.Health}");

                if (killed)
                {
                    Console.WriteLine("NPC defeated!");
                }

                // Trigger attack action
                _actionExecutor?.ExecuteZoneActions(ActionTrigger.Attack);
                return;
            }
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

    /// <summary>
    /// Initializes NPCs from zone objects.
    /// </summary>
    private void InitializeZoneNPCs()
    {
        _state.ZoneNPCs.Clear();

        if (_state.CurrentZone == null)
            return;

        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.PuzzleNPC)
            {
                var npc = NPC.FromZoneObject(obj);

                // Set NPC properties based on character type if it's a valid character
                if (npc.CharacterId < _gameData!.Characters.Count)
                {
                    var character = _gameData.Characters[npc.CharacterId];

                    // Enemy characters are hostile and chase the player
                    if (character.Type == CharacterType.Enemy)
                    {
                        npc.IsHostile = true;
                        npc.Behavior = NPCBehavior.Chasing;
                        npc.MoveCooldown = 0.4;  // Enemies move faster
                    }
                    // Friendly NPCs just wander
                    else if (character.Type == CharacterType.Friendly)
                    {
                        npc.Behavior = NPCBehavior.Wandering;
                        npc.MoveCooldown = 0.8;  // Friendlies move slower
                    }

                    // Apply character aux data for damage
                    if (character.AuxData != null)
                    {
                        npc.Damage = character.AuxData.Damage;
                    }

                    // Apply character weapon data for health
                    if (character.Weapon != null)
                    {
                        npc.MaxHealth = character.Weapon.Health;
                        npc.Health = character.Weapon.Health;
                    }
                }

                _state.ZoneNPCs.Add(npc);
            }
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

            // Sync NPC animations
            foreach (var npc in _state.ZoneNPCs)
            {
                npc.AnimationFrame = _state.AnimationFrame;
            }
        }

        // Update NPC AI
        UpdateNPCs(deltaTime);

        // Check for game over conditions
        if (_state.Health <= 0 && !_state.IsGameOver)
        {
            _state.IsGameOver = true;
            Console.WriteLine("Game Over!");
        }
    }

    private static readonly Random _random = new();

    private void UpdateNPCs(double deltaTime)
    {
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            // Update move timer
            npc.MoveTimer += deltaTime;

            // Check if it's time to move
            if (npc.MoveTimer < npc.MoveCooldown)
                continue;

            npc.MoveTimer = 0;

            switch (npc.Behavior)
            {
                case NPCBehavior.Wandering:
                    UpdateWanderingNPC(npc);
                    break;
                case NPCBehavior.Chasing:
                    UpdateChasingNPC(npc);
                    break;
                case NPCBehavior.Fleeing:
                    UpdateFleeingNPC(npc);
                    break;
                case NPCBehavior.Stationary:
                default:
                    // Face the player if nearby
                    var distToPlayer = npc.DistanceTo(_state.PlayerX, _state.PlayerY);
                    if (distToPlayer <= 3)
                    {
                        npc.Direction = GetDirectionToward(npc.X, npc.Y, _state.PlayerX, _state.PlayerY);
                    }
                    break;
            }

            // Hostile NPCs attack if adjacent to player
            if (npc.IsHostile)
            {
                npc.ActionTimer += deltaTime;
                if (npc.ActionTimer >= npc.AttackCooldown)
                {
                    var dist = npc.DistanceTo(_state.PlayerX, _state.PlayerY);
                    if (dist <= npc.AttackRange)
                    {
                        npc.ActionTimer = 0;
                        _state.Health -= npc.Damage;
                        Console.WriteLine($"NPC attacks! Player takes {npc.Damage} damage. Health: {_state.Health}");
                    }
                }
            }
        }
    }

    private void UpdateWanderingNPC(NPC npc)
    {
        // Random movement within wander radius of start position
        var direction = _random.Next(4);
        int dx = 0, dy = 0;

        switch (direction)
        {
            case 0: dy = -1; npc.Direction = Direction.Up; break;
            case 1: dy = 1; npc.Direction = Direction.Down; break;
            case 2: dx = -1; npc.Direction = Direction.Left; break;
            case 3: dx = 1; npc.Direction = Direction.Right; break;
        }

        var newX = npc.X + dx;
        var newY = npc.Y + dy;

        // Check if within wander radius
        if (Math.Abs(newX - npc.StartX) > npc.WanderRadius ||
            Math.Abs(newY - npc.StartY) > npc.WanderRadius)
            return;

        // Check if valid position
        if (IsValidNPCPosition(newX, newY, npc))
        {
            npc.X = newX;
            npc.Y = newY;
        }
    }

    private void UpdateChasingNPC(NPC npc)
    {
        // Move toward player
        var dx = Math.Sign(_state.PlayerX - npc.X);
        var dy = Math.Sign(_state.PlayerY - npc.Y);

        // Prefer horizontal or vertical based on distance
        if (_random.Next(2) == 0 && dx != 0)
        {
            npc.Direction = dx > 0 ? Direction.Right : Direction.Left;
            if (IsValidNPCPosition(npc.X + dx, npc.Y, npc))
            {
                npc.X += dx;
                return;
            }
        }

        if (dy != 0)
        {
            npc.Direction = dy > 0 ? Direction.Down : Direction.Up;
            if (IsValidNPCPosition(npc.X, npc.Y + dy, npc))
            {
                npc.Y += dy;
            }
        }
    }

    private void UpdateFleeingNPC(NPC npc)
    {
        // Move away from player
        var dx = -Math.Sign(_state.PlayerX - npc.X);
        var dy = -Math.Sign(_state.PlayerY - npc.Y);

        if (_random.Next(2) == 0 && dx != 0)
        {
            npc.Direction = dx > 0 ? Direction.Right : Direction.Left;
            if (IsValidNPCPosition(npc.X + dx, npc.Y, npc))
            {
                npc.X += dx;
                return;
            }
        }

        if (dy != 0)
        {
            npc.Direction = dy > 0 ? Direction.Down : Direction.Up;
            if (IsValidNPCPosition(npc.X, npc.Y + dy, npc))
            {
                npc.Y += dy;
            }
        }
    }

    private Direction GetDirectionToward(int fromX, int fromY, int toX, int toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;

        if (Math.Abs(dx) > Math.Abs(dy))
            return dx > 0 ? Direction.Right : Direction.Left;
        else
            return dy > 0 ? Direction.Down : Direction.Up;
    }

    private bool IsValidNPCPosition(int x, int y, NPC npc)
    {
        // Check bounds
        if (x < 0 || x >= _state.CurrentZone!.Width ||
            y < 0 || y >= _state.CurrentZone.Height)
            return false;

        // Check collision with player
        if (x == _state.PlayerX && y == _state.PlayerY)
            return false;

        // Check collision with other NPCs
        foreach (var other in _state.ZoneNPCs)
        {
            if (other != npc && other.IsEnabled && other.X == x && other.Y == y)
                return false;
        }

        // Check collision with middle layer tiles (walls/objects)
        var middleTile = _state.CurrentZone.GetTile(x, y, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            if (tile.IsObject && !tile.IsDraggable)
                return false;
        }

        return true;
    }

    private void Render()
    {
        if (_renderer == null || _state.CurrentZone == null)
            return;

        // Render zone
        _renderer.RenderZone(_state.CurrentZone, _state.CameraX, _state.CameraY);

        // Render zone items (crates, weapons on ground)
        RenderZoneItems();

        // Render NPCs
        RenderNPCs();

        // Render player character
        RenderPlayer();

        // Render HUD
        _renderer.RenderHUD(_state.Health, _state.MaxHealth, _state.Inventory, _state.SelectedWeapon, _state.SelectedItem);

        // Present frame
        _renderer.Present();
    }

    private void RenderZoneItems()
    {
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            // Only render item crates that haven't been picked up
            if (obj.Type != ZoneObjectType.CrateItem && obj.Type != ZoneObjectType.CrateWeapon)
                continue;

            // Check if already picked up (stored in zone state)
            if (_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                continue;

            // Check if within viewport
            if (obj.X < _state.CameraX || obj.X >= _state.CameraX + GameRenderer.ViewportTilesX ||
                obj.Y < _state.CameraY || obj.Y >= _state.CameraY + GameRenderer.ViewportTilesY)
                continue;

            // Render the item tile
            if (obj.Argument > 0 && obj.Argument < _gameData!.Tiles.Count)
            {
                _renderer!.RenderSprite(obj.Argument, obj.X, obj.Y, _state.CameraX, _state.CameraY);
            }
        }
    }

    private void RenderNPCs()
    {
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            // Check if NPC is within viewport
            if (npc.X < _state.CameraX || npc.X >= _state.CameraX + GameRenderer.ViewportTilesX ||
                npc.Y < _state.CameraY || npc.Y >= _state.CameraY + GameRenderer.ViewportTilesY)
                continue;

            // If CharacterId is a valid character, use character animation frames
            if (npc.CharacterId >= 0 && npc.CharacterId < _gameData!.Characters.Count)
            {
                var character = _gameData.Characters[npc.CharacterId];

                // Get animation frame based on direction
                var frames = npc.Direction switch
                {
                    Direction.Up => character.Frames.WalkUp,
                    Direction.Down => character.Frames.WalkDown,
                    Direction.Left => character.Frames.WalkLeft,
                    Direction.Right => character.Frames.WalkRight,
                    _ => character.Frames.WalkDown
                };

                if (frames != null && frames.Length > 0 && frames[0] != 0)
                {
                    var frameIndex = Math.Min(npc.AnimationFrame, frames.Length - 1);
                    var tileId = frames[frameIndex];
                    if (tileId < _gameData.Tiles.Count)
                    {
                        _renderer!.RenderSprite(tileId, npc.X, npc.Y, _state.CameraX, _state.CameraY);
                    }
                }
            }
            // If CharacterId is actually a tile ID (>= character count), render that tile directly
            else if (npc.CharacterId < _gameData!.Tiles.Count)
            {
                _renderer!.RenderSprite(npc.CharacterId, npc.X, npc.Y, _state.CameraX, _state.CameraY);
            }
        }
    }

    private void RenderPlayer()
    {
        // Find hero character for rendering
        // In Yoda Stories, the hero (Luke) is typically character 0 or has Hero type
        Character? heroChar = null;

        // First try to find by type
        foreach (var character in _gameData!.Characters)
        {
            if (character.Type == CharacterType.Hero)
            {
                heroChar = character;
                break;
            }
        }

        // Fallback to first character (usually the hero in Yoda Stories)
        if (heroChar == null && _gameData.Characters.Count > 0)
        {
            heroChar = _gameData.Characters[0];
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

            if (frames != null && frames.Length > 0 && frames[0] != 0)
            {
                var frameIndex = Math.Min(_state.AnimationFrame, frames.Length - 1);
                var tileId = frames[frameIndex];
                if (tileId < _gameData.Tiles.Count)
                {
                    _renderer!.RenderSprite(tileId, _state.PlayerX, _state.PlayerY, _state.CameraX, _state.CameraY);
                    return;
                }
            }
        }

        // Fallback: render a placeholder tile
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
