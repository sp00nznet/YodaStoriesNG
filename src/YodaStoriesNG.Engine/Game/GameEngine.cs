using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Audio;
using YodaStoriesNG.Engine.Bot;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;
using YodaStoriesNG.Engine.Rendering;
using YodaStoriesNG.Engine.UI;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Main game engine that coordinates all systems.
/// </summary>
public unsafe class GameEngine : IDisposable
{
    private GameData? _gameData;
    private GameState _state;
    private GameRenderer? _renderer;
    private ActionExecutor? _actionExecutor;
    private WorldGenerator? _worldGenerator;
    private MessageSystem _messages = new();
    private SoundManager? _sounds;
    private MissionBot? _bot;

    private bool _isRunning;
    private readonly string _dataPath;

    // Controller support
    private SDLGameController* _controller;
    private bool _controllerConnected;
    private const int ControllerDeadzone = 8000;  // Analog stick deadzone

    // Timing
    private const double TargetFrameTime = 1.0 / 60.0; // 60 FPS
    private const double AnimationFrameTime = 0.15; // 150ms per animation frame
    private double _controllerMoveTimer = 0;  // Rate limit controller movement

    /// <summary>
    /// Whether the bot is currently running.
    /// </summary>
    public bool IsBotRunning => _bot?.IsRunning ?? false;

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

        // Wire up action executor events (OnDialogue wired later, after init)
        _actionExecutor.OnMessage += (text) => _messages.ShowMessage(text, MessageType.Info);
        _actionExecutor.OnPlaySound += (soundId) => _sounds?.PlaySound(soundId);

        // Initialize sound manager
        _sounds = new SoundManager(_dataPath);
        _sounds.Initialize();

        // Load common sounds
        foreach (var sound in _gameData.Sounds)
        {
            _sounds.LoadSound(sound.Id, sound.FileName);
        }

        // Initialize game controller support
        InitializeController();

        // Start new game
        StartNewGame();

        return true;
    }

    /// <summary>
    /// Initializes game controller (Xbox controller support).
    /// </summary>
    private void InitializeController()
    {
        // Initialize SDL game controller subsystem
        if (SDL.InitSubSystem(0x00002000) < 0)  // SDL_INIT_GAMECONTROLLER
        {
            Console.WriteLine($"Failed to init game controller: {SDL.GetErrorS()}");
            return;
        }

        // Check for connected controllers
        var numJoysticks = SDL.NumJoysticks();
        Console.WriteLine($"Found {numJoysticks} joystick(s)");

        for (int i = 0; i < numJoysticks; i++)
        {
            if (SDL.IsGameController(i) != 0)
            {
                _controller = SDL.GameControllerOpen(i);
                if (_controller != null)
                {
                    var name = SDL.GameControllerNameS(_controller);
                    Console.WriteLine($"Controller connected: {name}");
                    _controllerConnected = true;
                    _messages.ShowMessage($"Controller: {name}", MessageType.System);
                    break;
                }
            }
        }

        if (!_controllerConnected)
        {
            Console.WriteLine("No game controller found (plug in Xbox controller to use)");
        }
    }

    /// <summary>
    /// Starts a new game.
    /// </summary>
    public void StartNewGame()
    {
        _state.Reset();
        _messages.Clear();

        // Suppress dialogue during initialization
        if (_actionExecutor != null)
            _actionExecutor.SuppressDialogue = true;

        // Give player starting weapon - lightsaber is always available at game start
        const int LIGHTSABER_TILE = 510;  // Lightsaber tile at atlas position 1291x338
        _state.Weapons.Add(LIGHTSABER_TILE);
        _state.CurrentWeaponIndex = 0;
        _state.SelectedWeapon = LIGHTSABER_TILE;

        // Generate the world
        _worldGenerator = new WorldGenerator(_gameData!);
        _worldGenerator.DumpDagobahInfo();  // Debug: print Dagobah zone analysis
        var world = _worldGenerator.GenerateWorld();

        // Welcome message - minimal startup hints (mission given by Yoda)
        _messages.ShowMessage("Find Yoda to receive your mission.", MessageType.System);

        // Load the starting zone (from Dagobah or landing zone)
        var startZoneId = world.StartingZoneId;
        if (startZoneId >= 0 && startZoneId < _gameData!.Zones.Count)
        {
            LoadZone(startZoneId);
        }
        else
        {
            // Fallback to first valid zone
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

        // Re-enable dialogue now that initialization is complete
        if (_actionExecutor != null)
        {
            _actionExecutor.SuppressDialogue = false;
            // Wire up dialogue event NOW, after init, so no dialogue appears during zone loading
            _actionExecutor.OnDialogue += (speaker, text) => _messages.ShowDialogue(speaker, text);
        }

        // Initialize bot (but don't start it)
        if (_worldGenerator != null)
        {
            _bot = new MissionBot(_state, _gameData!, _worldGenerator);
            _bot.OnActionRequested += HandleBotAction;
        }
    }

    /// <summary>
    /// Enables the automated mission bot.
    /// </summary>
    public void EnableBot()
    {
        if (_bot == null && _worldGenerator != null)
        {
            _bot = new MissionBot(_state, _gameData!, _worldGenerator);
            _bot.OnActionRequested += HandleBotAction;
        }
        _bot?.Start();
        _messages.ShowMessage("Bot ENABLED - Press B to disable", MessageType.System);
    }

    /// <summary>
    /// Disables the automated mission bot.
    /// </summary>
    public void DisableBot()
    {
        _bot?.Stop();
        _messages.ShowMessage("Bot DISABLED", MessageType.System);
    }

    /// <summary>
    /// Handles action requests from the bot.
    /// </summary>
    private void HandleBotAction(BotActionType type, int x, int y, Direction dir)
    {
        switch (type)
        {
            case BotActionType.Move:
                int dx, dy;
                // When x=0 and y=0, use direction for movement (bot convention for directional moves)
                if (x == 0 && y == 0)
                {
                    dx = 0;
                    dy = 0;
                    switch (dir)
                    {
                        case Direction.Up: dy = -1; break;
                        case Direction.Down: dy = 1; break;
                        case Direction.Left: dx = -1; break;
                        case Direction.Right: dx = 1; break;
                    }
                }
                else
                {
                    // Calculate dx/dy from current position to target
                    dx = Math.Sign(x - _state.PlayerX);
                    dy = Math.Sign(y - _state.PlayerY);
                }
                TryMovePlayer(dx, dy, dir, false);
                break;

            case BotActionType.Attack:
                _state.PlayerDirection = dir;
                PerformAttack();
                break;

            case BotActionType.Talk:
            case BotActionType.UseItem:
                _state.PlayerDirection = dir;
                UseItem();
                break;

            case BotActionType.UseXWing:
                TravelToPlanet();
                break;
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

        // Debug: show zone objects
        foreach (var obj in zone.Objects)
        {
            if (obj.Type == ZoneObjectType.DoorEntrance || obj.Type == ZoneObjectType.DoorExit ||
                obj.Type == ZoneObjectType.Teleporter || obj.Type == ZoneObjectType.Lock)
            {
                Console.WriteLine($"  Object: {obj.Type} at ({obj.X},{obj.Y}) -> zone {obj.Argument}");
            }
        }

        // Play zone entry sound (but don't spam messages)
        _sounds?.PlaySound(SoundManager.SoundDoor);

        // Debug: Show action count for zones with scripts
        if (zone.Actions.Any(a => a.Instructions.Count > 0))
        {
            int totalInstructions = zone.Actions.Sum(a => a.Instructions.Count);
            Console.WriteLine($"  Zone has {zone.Actions.Count} scripts ({totalInstructions} instructions)");
        }

        // Initialize NPCs from zone objects
        InitializeZoneNPCs();

        // Spawn Yoda if this is his zone
        SpawnYodaIfNeeded(zoneId);

        // Check for X-Wing in this zone
        CheckForXWing(zoneId);

        // Reset camera for zone
        UpdateCamera();

        // Execute zone entry actions (suppress dialogue - it should only show from player interaction)
        if (_actionExecutor != null)
        {
            _actionExecutor.SuppressDialogue = true;
            _actionExecutor.ExecuteZoneActions(ActionTrigger.ZoneEnter);
            _actionExecutor.SuppressDialogue = false;
        }

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

                // Controller events
                case SDLEventType.Controllerdeviceadded:
                    if (!_controllerConnected)
                    {
                        _controller = SDL.GameControllerOpen(evt.Cdevice.Which);
                        if (_controller != null)
                        {
                            var name = SDL.GameControllerNameS(_controller);
                            Console.WriteLine($"Controller connected: {name}");
                            _controllerConnected = true;
                            _messages.ShowMessage($"Controller: {name}", MessageType.System);
                        }
                    }
                    break;

                case SDLEventType.Controllerdeviceremoved:
                    if (_controllerConnected)
                    {
                        SDL.GameControllerClose(_controller);
                        _controller = null;
                        _controllerConnected = false;
                        Console.WriteLine("Controller disconnected");
                        _messages.ShowMessage("Controller disconnected", MessageType.System);
                    }
                    break;

                case SDLEventType.Controllerbuttondown:
                    HandleControllerButton(evt.Cbutton.Button, true);
                    break;

                case SDLEventType.Controllerbuttonup:
                    HandleControllerButton(evt.Cbutton.Button, false);
                    break;
            }
        }

        // Handle analog stick movement (called every frame)
        if (_controllerConnected && _controller != null)
        {
            HandleControllerAnalog();
        }
    }

    /// <summary>
    /// Handles controller button presses.
    /// Xbox controller mapping:
    /// - A: Action/Talk (Space)
    /// - B: Cancel/Back
    /// - X: Use Item
    /// - Y: Show Objective
    /// - LB/RB: Cycle weapons
    /// - Start: Restart
    /// - D-Pad: Movement
    /// </summary>
    private void HandleControllerButton(byte button, bool pressed)
    {
        if (!pressed) return;  // Only handle button down

        // SDL GameController button constants
        const byte SDL_CONTROLLER_BUTTON_A = 0;
        const byte SDL_CONTROLLER_BUTTON_B = 1;
        const byte SDL_CONTROLLER_BUTTON_X = 2;
        const byte SDL_CONTROLLER_BUTTON_Y = 3;
        const byte SDL_CONTROLLER_BUTTON_BACK = 4;
        const byte SDL_CONTROLLER_BUTTON_START = 6;
        const byte SDL_CONTROLLER_BUTTON_LEFTSHOULDER = 9;
        const byte SDL_CONTROLLER_BUTTON_RIGHTSHOULDER = 10;
        const byte SDL_CONTROLLER_BUTTON_DPAD_UP = 11;
        const byte SDL_CONTROLLER_BUTTON_DPAD_DOWN = 12;
        const byte SDL_CONTROLLER_BUTTON_DPAD_LEFT = 13;
        const byte SDL_CONTROLLER_BUTTON_DPAD_RIGHT = 14;

        switch (button)
        {
            case SDL_CONTROLLER_BUTTON_A:
                UseItem();  // A = Action/Talk
                break;
            case SDL_CONTROLLER_BUTTON_B:
                if (_messages.HasDialogue)
                    _messages.DismissDialogue();
                break;
            case SDL_CONTROLLER_BUTTON_X:
                TravelToPlanet();  // X = Travel
                break;
            case SDL_CONTROLLER_BUTTON_Y:
                ShowMissionObjective();  // Y = Objective
                break;
            case SDL_CONTROLLER_BUTTON_START:
                StartNewGame();  // Start = Restart
                break;
            case SDL_CONTROLLER_BUTTON_BACK:
                _isRunning = false;  // Back = Quit
                break;
            case SDL_CONTROLLER_BUTTON_LEFTSHOULDER:
            case SDL_CONTROLLER_BUTTON_RIGHTSHOULDER:
                ToggleWeapon();  // Shoulder buttons = Toggle weapon
                break;
            case SDL_CONTROLLER_BUTTON_DPAD_UP:
                TryMovePlayer(0, -1, Direction.Up, false);
                break;
            case SDL_CONTROLLER_BUTTON_DPAD_DOWN:
                TryMovePlayer(0, 1, Direction.Down, false);
                break;
            case SDL_CONTROLLER_BUTTON_DPAD_LEFT:
                TryMovePlayer(-1, 0, Direction.Left, false);
                break;
            case SDL_CONTROLLER_BUTTON_DPAD_RIGHT:
                TryMovePlayer(1, 0, Direction.Right, false);
                break;
        }
    }

    /// <summary>
    /// Handles controller analog stick movement.
    /// </summary>
    private void HandleControllerAnalog()
    {
        // Rate limit movement
        _controllerMoveTimer -= TargetFrameTime;
        if (_controllerMoveTimer > 0) return;

        // Get left stick axes
        var leftX = SDL.GameControllerGetAxis(_controller, SDLGameControllerAxis.Leftx);
        var leftY = SDL.GameControllerGetAxis(_controller, SDLGameControllerAxis.Lefty);

        // Apply deadzone
        if (Math.Abs(leftX) < ControllerDeadzone) leftX = 0;
        if (Math.Abs(leftY) < ControllerDeadzone) leftY = 0;

        // Determine movement direction
        int dx = 0, dy = 0;
        var dir = _state.PlayerDirection;

        if (Math.Abs(leftX) > Math.Abs(leftY))
        {
            // Horizontal movement
            if (leftX > 0) { dx = 1; dir = Direction.Right; }
            else if (leftX < 0) { dx = -1; dir = Direction.Left; }
        }
        else
        {
            // Vertical movement
            if (leftY > 0) { dy = 1; dir = Direction.Down; }
            else if (leftY < 0) { dy = -1; dir = Direction.Up; }
        }

        if (dx != 0 || dy != 0)
        {
            TryMovePlayer(dx, dy, dir, false);
            // Movement rate based on stick magnitude
            var magnitude = Math.Max(Math.Abs(leftX), Math.Abs(leftY));
            _controllerMoveTimer = 0.15 - (magnitude / 32768.0) * 0.1;  // 0.05 to 0.15 seconds
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
        const int SDLK_TAB = 9;
        const int SDLK_a = 97;
        const int SDLK_b = 98;
        const int SDLK_d = 100;
        const int SDLK_f = 102;
        const int SDLK_m = 109;
        const int SDLK_n = 110;
        const int SDLK_o = 111;
        const int SDLK_p = 112;
        const int SDLK_r = 114;
        const int SDLK_s = 115;
        const int SDLK_w = 119;
        const int SDLK_x = 120;

        switch (keyCode)
        {
            case SDLK_ESCAPE:
                _isRunning = false;
                break;

            case SDLK_UP:
            case SDLK_w:
                TryMovePlayer(0, -1, Direction.Up, IsShiftHeld());
                break;

            case SDLK_DOWN:
            case SDLK_s:
                TryMovePlayer(0, 1, Direction.Down, IsShiftHeld());
                break;

            case SDLK_LEFT:
            case SDLK_a:
                TryMovePlayer(-1, 0, Direction.Left, IsShiftHeld());
                break;

            case SDLK_RIGHT:
            case SDLK_d:
                TryMovePlayer(1, 0, Direction.Right, IsShiftHeld());
                break;

            case SDLK_SPACE:
                // Use item or attack
                UseItem();
                break;

            case >= SDLK_1 and <= SDLK_8:
                // Select inventory item (keys 1-8)
                var slot = keyCode - SDLK_1;
                if (slot < _state.Inventory.Count)
                {
                    _state.SelectedItem = _state.Inventory[slot];
                    var itemName = GetTileName(_state.Inventory[slot]) ?? $"Item {slot + 1}";
                    _messages.ShowMessage($"Selected: {itemName}", MessageType.Info);
                }
                else
                {
                    _messages.ShowMessage($"Slot {slot + 1} is empty", MessageType.Info);
                }
                break;

            case SDLK_r:
                // Restart/new game
                StartNewGame();
                break;

            case SDLK_x:
                // X-Wing travel
                TravelToPlanet();
                break;

            case SDLK_n:
                // Next zone (debug)
                LoadZone((_state.CurrentZoneId + 1) % _gameData!.Zones.Count);
                break;

            case SDLK_p:
                // Previous zone (debug)
                LoadZone((_state.CurrentZoneId - 1 + _gameData!.Zones.Count) % _gameData.Zones.Count);
                break;

            case SDLK_m:
                // Toggle sound mute
                if (_sounds != null)
                {
                    _sounds.ToggleMute();
                    _messages.ShowMessage(_sounds.IsMuted ? "Sound OFF" : "Sound ON", MessageType.System);
                }
                break;

            case SDLK_f:  // F key - find zone with NPCs
                FindZoneWithContent();
                break;

            case SDLK_o:  // O key - show current objective
                ShowMissionObjective();
                break;

            case SDLK_b:  // B key - toggle bot
                if (IsBotRunning)
                    DisableBot();
                else
                    EnableBot();
                Console.WriteLine($"Bot: {(IsBotRunning ? "ENABLED" : "DISABLED")}");
                break;

            case SDLK_TAB:  // Tab - toggle weapon
                ToggleWeapon();
                break;
        }
    }

    private void ToggleWeapon()
    {
        if (_state.Weapons.Count == 0)
        {
            _messages.ShowMessage("No weapons!", MessageType.Info);
            return;
        }

        _state.CurrentWeaponIndex = (_state.CurrentWeaponIndex + 1) % _state.Weapons.Count;
        var weaponId = _state.Weapons[_state.CurrentWeaponIndex];

        if (weaponId == 0)
        {
            _state.SelectedWeapon = null;
            _messages.ShowMessage("Equipped: Fists", MessageType.Info);
        }
        else
        {
            _state.SelectedWeapon = weaponId;
            var weaponName = GetTileName(weaponId) ?? $"Weapon";
            _messages.ShowMessage($"Equipped: {weaponName}", MessageType.Info);
        }
    }

    private void ShowMissionObjective()
    {
        if (_worldGenerator?.CurrentWorld == null)
        {
            _messages.ShowMessage("No active mission.", MessageType.Info);
            return;
        }

        var world = _worldGenerator.CurrentWorld;
        var mission = world.Mission;

        if (mission == null)
        {
            _messages.ShowMessage("Explore the area.", MessageType.Info);
            return;
        }

        // Show mission name
        _messages.ShowMessage($"Mission: {mission.Name}", MessageType.System);

        // Show current objective
        var objective = world.GetCurrentObjective();
        _messages.ShowMessage($"Objective: {objective}", MessageType.Info);

        // Show progress
        if (mission.PuzzleChain.Count > 0)
        {
            var completed = mission.PuzzleChain.Count(s => s.IsCompleted);
            _messages.ShowMessage($"Progress: {completed}/{mission.PuzzleChain.Count} steps", MessageType.Info);
        }

        // Show current required item if applicable
        var currentStep = mission.CurrentPuzzleStep;
        if (currentStep != null && currentStep.RequiredItemId > 0)
        {
            var itemName = GetTileName(currentStep.RequiredItemId) ?? $"Item #{currentStep.RequiredItemId}";
            bool hasItem = _state.HasItem(currentStep.RequiredItemId);
            _messages.ShowMessage($"Need: {itemName} {(hasItem ? "(owned)" : "(not found)")}", MessageType.Info);
        }
    }

    private void FindZoneWithContent()
    {
        // Search for next zone with NPCs or items starting from current zone
        for (int i = 1; i < _gameData!.Zones.Count; i++)
        {
            var zoneId = (_state.CurrentZoneId + i) % _gameData.Zones.Count;
            var zone = _gameData.Zones[zoneId];

            if (zone.Width == 0 || zone.Height == 0)
                continue;

            int npcCount = 0;
            int itemCount = 0;
            int friendlyCount = 0;
            int enemyCount = 0;

            foreach (var obj in zone.Objects)
            {
                if (obj.Type == ZoneObjectType.PuzzleNPC)
                {
                    npcCount++;
                    // Check if valid character and what type
                    if (obj.Argument < _gameData.Characters.Count)
                    {
                        var charType = _gameData.Characters[obj.Argument].Type;
                        if (charType == CharacterType.Enemy)
                            enemyCount++;
                        else if (charType == CharacterType.Friendly)
                            friendlyCount++;
                    }
                }
                if (obj.Type == ZoneObjectType.CrateItem || obj.Type == ZoneObjectType.CrateWeapon)
                    itemCount++;
            }

            if (npcCount > 0 || itemCount > 0)
            {
                Console.WriteLine($"Found zone {zoneId} with {npcCount} NPCs ({friendlyCount} friendly, {enemyCount} hostile), {itemCount} items");
                LoadZone(zoneId);
                return;
            }
        }

        _messages.ShowMessage("No zones with content found", MessageType.System);
    }

    private bool IsShiftHeld()
    {
        // Check if Shift key is held using SDL
        var modState = SDL.GetModState();
        return (modState & SDLKeymod.Shift) != 0;
    }

    /// <summary>
    /// Tries to pull a draggable block from behind the player to where the player was.
    /// </summary>
    private void TryPullBlock(int oldPlayerX, int oldPlayerY, int moveDirectionX, int moveDirectionY)
    {
        // Block behind player (opposite to movement direction)
        int behindX = oldPlayerX - moveDirectionX;
        int behindY = oldPlayerY - moveDirectionY;

        // Check bounds
        if (behindX < 0 || behindX >= _state.CurrentZone!.Width ||
            behindY < 0 || behindY >= _state.CurrentZone.Height)
        {
            return;  // Nothing to pull from off-screen
        }

        // Check if there's a draggable block behind
        var behindTile = _state.CurrentZone.GetTile(behindX, behindY, 1);
        if (behindTile == 0xFFFF || behindTile >= _gameData!.Tiles.Count)
        {
            return;  // No tile there
        }

        var tile = _gameData.Tiles[behindTile];
        if (!tile.IsDraggable)
        {
            return;  // Not draggable
        }

        // Pull the block to where the player was standing
        _state.CurrentZone.SetTile(oldPlayerX, oldPlayerY, 1, behindTile);
        _state.CurrentZone.SetTile(behindX, behindY, 1, 0xFFFF);
        _messages.ShowMessage("*pull*", MessageType.Info);
    }

    private void TryMovePlayer(int dx, int dy, Direction direction, bool tryPull = false)
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

            if (tile.IsDraggable)
            {
                // Try to push the block
                var pushX = newX + dx;
                var pushY = newY + dy;

                // Check if push destination is valid
                if (pushX >= 0 && pushX < _state.CurrentZone.Width &&
                    pushY >= 0 && pushY < _state.CurrentZone.Height)
                {
                    var destTile = _state.CurrentZone.GetTile(pushX, pushY, 1);
                    if (destTile == 0xFFFF || (destTile < _gameData.Tiles.Count && !_gameData.Tiles[destTile].IsObject))
                    {
                        // Push the block
                        _state.CurrentZone.SetTile(pushX, pushY, 1, middleTile);
                        _state.CurrentZone.SetTile(newX, newY, 1, 0xFFFF);
                        _messages.ShowMessage("*push*", MessageType.Info);
                        // Continue to move player into the now-empty space
                    }
                    else
                    {
                        // Can't push - something blocking
                        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Bump);
                        return;
                    }
                }
                else
                {
                    // Can't push off edge
                    _actionExecutor?.ExecuteZoneActions(ActionTrigger.Bump);
                    return;
                }
            }
            else if (tile.IsObject)
            {
                // Collision - trigger bump action
                _actionExecutor?.ExecuteZoneActions(ActionTrigger.Bump);
                return;
            }
        }

        // Check collision with NPCs
        foreach (var npc in _state.ZoneNPCs)
        {
            if (npc.IsEnabled && npc.IsAlive && npc.X == newX && npc.Y == newY)
            {
                // Can't walk through NPCs
                return;
            }
        }

        // Remember old position for pull
        var oldX = _state.PlayerX;
        var oldY = _state.PlayerY;

        // Move player
        _state.PlayerX = newX;
        _state.PlayerY = newY;

        // Pull block if shift is held
        if (tryPull)
        {
            TryPullBlock(oldX, oldY, dx, dy);
        }

        // Update camera
        UpdateCamera();

        // Execute walk actions
        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);

        // Check for zone objects at new position
        CheckZoneObjects();

        // Check for Item tiles on layer 1 that can be picked up
        var objTile = _state.CurrentZone.GetTile(newX, newY, 1);
        if (objTile != 0xFFFF && objTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[objTile];
            Console.WriteLine($"Standing on tile {objTile}: floor={tile.IsFloor}, obj={tile.IsObject}, item={tile.IsItem}, char={tile.IsCharacter}");

            // Auto-pickup Item tiles (not Objects that block movement)
            if (tile.IsItem && !tile.IsObject)
            {
                var key = $"{_state.CurrentZoneId}_{newX}_{newY}_tile";
                if (!_state.CollectedObjects.Contains(key))
                {
                    // Pick up the item
                    _state.AddItem(objTile);
                    _state.CollectedObjects.Add(key);

                    // Remove the tile from the map
                    _state.CurrentZone.SetTile(newX, newY, 1, 0xFFFF);

                    var itemName = GetTileName(objTile) ?? $"Item";
                    _messages.ShowPickup(itemName);
                    _sounds?.PlaySound(SoundManager.SoundPickup);
                    Console.WriteLine($"Auto-picked up tile item {objTile} ({itemName}) at ({newX},{newY})");
                }
            }
        }

        // Check for nearby friendly NPCs
        CheckNearbyNPCs();
    }

    private void CheckNearbyNPCs()
    {
        // Get position in front of player
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

            if (npc.X == targetX && npc.Y == targetY && !npc.IsHostile)
            {
                var npcName = GetCharacterName(npc.CharacterId) ?? "Someone";
                _messages.ShowMessage($"[Space] Talk to {npcName}", MessageType.Info);
                return;
            }
        }
    }

    private void HandleZoneTransition(int dx, int dy)
    {
        // Check for transition objects at player position and edge position
        var edgeX = _state.PlayerX + dx;
        var edgeY = _state.PlayerY + dy;

        // First check for door/teleporter objects
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            // Check if this is a transition object at player pos or just past edge
            bool atPlayerPos = obj.X == _state.PlayerX && obj.Y == _state.PlayerY;
            bool atEdge = obj.X == Math.Clamp(edgeX, 0, _state.CurrentZone.Width - 1) &&
                          obj.Y == Math.Clamp(edgeY, 0, _state.CurrentZone.Height - 1);

            if (!atPlayerPos && !atEdge)
                continue;

            switch (obj.Type)
            {
                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                case ZoneObjectType.VehicleToSecondary:
                case ZoneObjectType.VehicleToPrimary:
                case ZoneObjectType.XWingFromDagobah:
                case ZoneObjectType.XWingToDagobah:
                case ZoneObjectType.Teleporter:
                    // Handle return doors (65535 means go back)
                    int destZoneId = obj.Argument;
                    if (destZoneId == 65535 || destZoneId == 0xFFFF)
                    {
                        // Return to parent zone (for rooms)
                        if (_worldGenerator?.CurrentWorld != null &&
                            _worldGenerator.CurrentWorld.RoomParents.TryGetValue(_state.CurrentZoneId, out var parentId))
                        {
                            destZoneId = parentId;
                        }
                        else if (_state.PreviousZoneId >= 0)
                        {
                            destZoneId = _state.PreviousZoneId;
                        }
                        else
                        {
                            _messages.ShowMessage("Can't go back", MessageType.Info);
                            return;
                        }
                    }

                    if (destZoneId > 0 && destZoneId < _gameData!.Zones.Count)
                    {
                        Console.WriteLine($"Door transition: {obj.Type} from zone {_state.CurrentZoneId} to zone {destZoneId}");

                        // Always track previous zone for return doors
                        var oldZoneId = _state.CurrentZoneId;
                        _state.PreviousZoneId = oldZoneId;

                        // Find spawn point in destination zone
                        var destZone = _gameData.Zones[destZoneId];
                        int? spawnX = null, spawnY = null;

                        // First, look for a door/spawn that leads back to where we came from
                        foreach (var destObj in destZone.Objects)
                        {
                            // For rooms entered via DoorEntrance, look for DoorExit (return door)
                            // For outdoor zones, look for SpawnLocation first
                            if (destObj.Type == ZoneObjectType.SpawnLocation)
                            {
                                spawnX = destObj.X;
                                spawnY = destObj.Y;
                                Console.WriteLine($"  Spawning at spawn point ({spawnX},{spawnY})");
                                break;
                            }

                            if ((destObj.Type == ZoneObjectType.DoorEntrance ||
                                 destObj.Type == ZoneObjectType.DoorExit) &&
                                (destObj.Argument == oldZoneId || destObj.Argument == 65535))
                            {
                                // Spawn near this door, offset based on door position
                                spawnX = destObj.X;
                                spawnY = destObj.Y;

                                // Offset away from the door (usually doors are at edges)
                                if (destObj.Y == 0) spawnY = 1;  // Door at top, spawn below
                                else if (destObj.Y >= destZone.Height - 1) spawnY = destZone.Height - 2;  // Door at bottom
                                else if (destObj.X == 0) spawnX = 1;  // Door at left
                                else if (destObj.X >= destZone.Width - 1) spawnX = destZone.Width - 2;  // Door at right
                                else spawnY = Math.Min(spawnY.Value + 1, destZone.Height - 2);  // Default: below door

                                Console.WriteLine($"  Spawning near door at ({spawnX},{spawnY})");
                                break;
                            }
                        }

                        // Fallback: look for spawn location
                        if (!spawnX.HasValue)
                        {
                            foreach (var destObj in destZone.Objects)
                            {
                                if (destObj.Type == ZoneObjectType.SpawnLocation)
                                {
                                    spawnX = destObj.X;
                                    spawnY = destObj.Y;
                                    Console.WriteLine($"  Spawning at spawn location ({spawnX},{spawnY})");
                                    break;
                                }
                            }
                        }

                        // Default spawn position
                        if (!spawnX.HasValue)
                        {
                            spawnX = destZone.Width / 2;
                            spawnY = destZone.Height / 2;
                            Console.WriteLine($"  Spawning at center ({spawnX},{spawnY})");
                        }

                        LoadZone(destZoneId, spawnX, spawnY);
                        return;
                    }
                    break;
            }
        }

        // Check for edge transition using world generator connections
        if (_worldGenerator != null)
        {
            var direction = (dx, dy) switch
            {
                (-1, 0) => Direction.Left,
                (1, 0) => Direction.Right,
                (0, -1) => Direction.Up,
                (0, 1) => Direction.Down,
                _ => Direction.Down
            };

            var connectedZoneId = _worldGenerator.GetConnectedZone(_state.CurrentZoneId, direction);
            Console.WriteLine($"Edge transition {direction}: connected zone = {connectedZoneId}");

            if (connectedZoneId.HasValue && connectedZoneId.Value < _gameData!.Zones.Count)
            {
                var destZone = _gameData.Zones[connectedZoneId.Value];

                // Spawn on opposite edge
                int spawnX, spawnY;
                if (dx < 0) { spawnX = destZone.Width - 2; spawnY = Math.Clamp(_state.PlayerY, 1, destZone.Height - 2); }
                else if (dx > 0) { spawnX = 1; spawnY = Math.Clamp(_state.PlayerY, 1, destZone.Height - 2); }
                else if (dy < 0) { spawnX = Math.Clamp(_state.PlayerX, 1, destZone.Width - 2); spawnY = destZone.Height - 2; }
                else { spawnX = Math.Clamp(_state.PlayerX, 1, destZone.Width - 2); spawnY = 1; }

                _state.PreviousZoneId = _state.CurrentZoneId;
                LoadZone(connectedZoneId.Value, spawnX, spawnY);
                return;
            }
        }

        // Edge of zone with no transition
        _messages.ShowMessage("Can't go that way", MessageType.Info);
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
                        var itemName = GetTileName(obj.Argument) ?? $"Item #{obj.Argument}";
                        _messages.ShowPickup(itemName);
                        _sounds?.PlaySound(SoundManager.SoundPickup);
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    // Pick up weapon (if not already collected)
                    if (obj.Argument > 0 && !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        // Add to weapons list if not already owned
                        if (!_state.Weapons.Contains(obj.Argument))
                        {
                            _state.Weapons.Add(obj.Argument);
                        }
                        // Auto-equip the new weapon
                        _state.CurrentWeaponIndex = _state.Weapons.IndexOf(obj.Argument);
                        _state.SelectedWeapon = obj.Argument;
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        var weaponName = GetTileName(obj.Argument) ?? $"Weapon";
                        _messages.ShowPickup(weaponName);
                        _messages.ShowMessage("Press Tab to switch weapons", MessageType.Info);
                        _sounds?.PlaySound(SoundManager.SoundPickup);
                    }
                    break;

                case ZoneObjectType.LocatorItem:
                    // Pick up locator/R2D2 (if not already collected)
                    if (!_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        // The locator is an item that gives hints - add it to inventory
                        if (obj.Argument > 0)
                        {
                            _state.AddItem(obj.Argument);
                        }
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        _state.HasLocator = true;
                        var itemName = GetTileName(obj.Argument) ?? "Locator Droid";
                        _messages.ShowPickup(itemName);
                        _messages.ShowMessage("Use the locator from your inventory for hints!", MessageType.Info);
                        _sounds?.PlaySound(SoundManager.SoundPickup);
                    }
                    break;

                case ZoneObjectType.Teleporter:
                case ZoneObjectType.VehicleToSecondary:
                case ZoneObjectType.VehicleToPrimary:
                case ZoneObjectType.XWingFromDagobah:
                case ZoneObjectType.XWingToDagobah:
                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                    // Handle 65535 (0xFFFF) as "return to previous zone"
                    int destZoneId = obj.Argument;
                    if (destZoneId == 65535 || destZoneId == -1)
                    {
                        destZoneId = _state.PreviousZoneId;
                        Console.WriteLine($"Return door: going back to zone {destZoneId}");
                    }

                    if (destZoneId > 0 && destZoneId < _gameData!.Zones.Count)
                    {
                        Console.WriteLine($"Door transition: zone {destZoneId}");

                        // Track previous zone only when entering through a DoorEntrance (not returning)
                        if (obj.Type == ZoneObjectType.DoorEntrance && obj.Argument != 65535)
                        {
                            _state.PreviousZoneId = _state.CurrentZoneId;
                            Console.WriteLine($"  Set return zone to {_state.PreviousZoneId}");
                        }

                        // Find spawn point in destination zone
                        var destZone = _gameData.Zones[destZoneId];
                        int? spawnX = null, spawnY = null;

                        // Look for a door that leads back to current zone, or a spawn point
                        foreach (var destObj in destZone.Objects)
                        {
                            // Check for door leading back, or door with 65535 (return door)
                            if ((destObj.Type == ZoneObjectType.DoorEntrance || destObj.Type == ZoneObjectType.DoorExit) &&
                                (destObj.Argument == _state.CurrentZoneId || destObj.Argument == 65535))
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

                        LoadZone(destZoneId, spawnX, spawnY);
                    }
                    break;

                case ZoneObjectType.Trigger:
                    _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);
                    break;
            }
        }
    }

    private void UseItem()
    {
        // If dialogue is active, dismiss it
        if (_messages.HasDialogue)
        {
            _messages.DismissDialogue();
            return;
        }

        if (_state.SelectedItem.HasValue)
        {
            var usedItemId = _state.SelectedItem.Value;

            // Check if this is a locator/R2D2 item - these give hints
            if (usedItemId < _gameData!.Tiles.Count)
            {
                var itemTile = _gameData.Tiles[usedItemId];
                if ((itemTile.Flags & TileFlags.ItemLocator) != 0 || _state.HasLocator && IsLocatorTile(usedItemId))
                {
                    ShowLocatorHint();
                    return;
                }
            }

            // Set the placed item context for action conditions
            if (_actionExecutor != null)
            {
                _actionExecutor.PlacedItemId = usedItemId;

                // Also set NPC context if there's a nearby friendly NPC
                var nearbyNpc = FindNearbyNPC(friendlyOnly: true);
                if (nearbyNpc != null)
                {
                    _actionExecutor.InteractingNpcId = nearbyNpc.CharacterId;
                    Console.WriteLine($"UseItem: Set InteractingNpcId to {nearbyNpc.CharacterId} for nearby NPC");

                    // Check if this advances the mission
                    TryAdvanceMission(usedItemId, nearbyNpc);
                }
            }

            _actionExecutor?.ExecuteZoneActions(ActionTrigger.UseItem);

            // Clear the context after execution
            if (_actionExecutor != null)
            {
                _actionExecutor.PlacedItemId = null;
                _actionExecutor.InteractingNpcId = null;
            }
        }
        else
        {
            // Check for friendly NPC interaction first
            if (!TryInteractWithNPC())
            {
                // Check for items/objects to interact with
                if (!TryInteractWithObject())
                {
                    // Attack in facing direction
                    PerformAttack();
                }
            }
        }
    }

    /// <summary>
    /// Checks if using an item with an NPC advances the mission.
    /// </summary>
    private void TryAdvanceMission(int itemId, NPC npc)
    {
        if (_worldGenerator?.CurrentWorld == null)
            return;

        var world = _worldGenerator.CurrentWorld;
        var mission = world.Mission;
        if (mission == null || mission.IsCompleted)
            return;

        var currentStep = mission.CurrentPuzzleStep;
        if (currentStep == null)
            return;

        // Check if the used item matches what's needed for the current step
        if (currentStep.RequiredItemId == itemId)
        {
            Console.WriteLine($"Mission: Used correct item {itemId} for step {mission.CurrentStep + 1}");

            // Remove the used item from inventory
            _state.RemoveItem(itemId);
            var usedItemName = GetTileName(itemId) ?? "item";

            // Give the reward item if any
            if (currentStep.RewardItemId > 0)
            {
                _state.AddItem(currentStep.RewardItemId);
                var rewardName = GetTileName(currentStep.RewardItemId) ?? "item";
                _messages.ShowMessage($"Traded {usedItemName} for {rewardName}!", MessageType.Pickup);
                _sounds?.PlaySound(SoundManager.SoundPickup);

                // Auto-select the new item
                _state.SelectedItem = currentStep.RewardItemId;
            }
            else
            {
                _messages.ShowMessage($"Used {usedItemName} successfully!", MessageType.Info);
            }

            // Advance the mission
            bool missionComplete = world.AdvanceMission();

            if (missionComplete)
            {
                _messages.ShowMessage("MISSION COMPLETE!", MessageType.System);
                _messages.ShowMessage("Return to Yoda on Dagobah.", MessageType.Info);
                _sounds?.PlaySound(SoundManager.SoundPickup);
            }
            else
            {
                // Show next objective
                var nextStep = mission.CurrentPuzzleStep;
                if (nextStep != null && !string.IsNullOrEmpty(nextStep.Hint))
                {
                    _messages.ShowMessage($"Next: {nextStep.Hint}", MessageType.Info);
                }
            }
        }
    }

    /// <summary>
    /// Finds a nearby NPC (adjacent to player).
    /// </summary>
    private NPC? FindNearbyNPC(bool friendlyOnly = false)
    {
        // Get position in front of player
        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (_state.PlayerDirection)
        {
            case Direction.Up: targetY--; break;
            case Direction.Down: targetY++; break;
            case Direction.Left: targetX--; break;
            case Direction.Right: targetX++; break;
        }

        // Check for NPC at target position or adjacent to player
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            if (friendlyOnly && npc.IsHostile)
                continue;

            // Check if NPC is at the target position OR adjacent to player
            bool atTarget = (npc.X == targetX && npc.Y == targetY);
            bool adjacent = Math.Abs(npc.X - _state.PlayerX) <= 1 &&
                           Math.Abs(npc.Y - _state.PlayerY) <= 1 &&
                           !(npc.X == _state.PlayerX && npc.Y == _state.PlayerY);

            if (atTarget || adjacent)
            {
                return npc;
            }
        }

        return null;
    }

    private bool TryInteractWithObject()
    {
        // Get position in front of player
        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (_state.PlayerDirection)
        {
            case Direction.Up: targetY--; break;
            case Direction.Down: targetY++; break;
            case Direction.Left: targetX--; break;
            case Direction.Right: targetX++; break;
        }

        // Check bounds
        if (targetX < 0 || targetX >= _state.CurrentZone!.Width ||
            targetY < 0 || targetY >= _state.CurrentZone.Height)
            return false;

        // Check for items at target position
        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.X != targetX || obj.Y != targetY)
                continue;

            switch (obj.Type)
            {
                case ZoneObjectType.CrateItem:
                    if (obj.Argument > 0 && !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        _state.AddItem(obj.Argument);
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        var itemName = GetTileName(obj.Argument) ?? $"Item";
                        _messages.ShowPickup(itemName);
                        _sounds?.PlaySound(SoundManager.SoundPickup);
                        return true;
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    if (obj.Argument > 0 && !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        if (!_state.Weapons.Contains(obj.Argument))
                        {
                            _state.Weapons.Add(obj.Argument);
                        }
                        _state.CurrentWeaponIndex = _state.Weapons.IndexOf(obj.Argument);
                        _state.SelectedWeapon = obj.Argument;
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        var weaponName = GetTileName(obj.Argument) ?? $"Weapon";
                        _messages.ShowPickup(weaponName);
                        _messages.ShowMessage("Press Tab to switch weapons", MessageType.Info);
                        _sounds?.PlaySound(SoundManager.SoundPickup);
                        return true;
                    }
                    break;

                case ZoneObjectType.LocatorItem:
                    // Pick up locator/R2D2 (if not already collected)
                    if (!_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    {
                        if (obj.Argument > 0)
                        {
                            _state.AddItem(obj.Argument);
                        }
                        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
                        _state.HasLocator = true;
                        var locatorName = GetTileName(obj.Argument) ?? "Locator Droid";
                        _messages.ShowPickup(locatorName);
                        _messages.ShowMessage("Use the locator from your inventory for hints!", MessageType.Info);
                        _sounds?.PlaySound(SoundManager.SoundPickup);
                        return true;
                    }
                    break;
            }
        }

        return false;
    }

    private bool TryInteractWithNPC()
    {
        // Get position in front of player
        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (_state.PlayerDirection)
        {
            case Direction.Up: targetY--; break;
            case Direction.Down: targetY++; break;
            case Direction.Left: targetX--; break;
            case Direction.Right: targetX++; break;
        }

        // Check for NPC at target position or adjacent to player
        Console.WriteLine($"TryInteractWithNPC: player at ({_state.PlayerX},{_state.PlayerY}) facing {_state.PlayerDirection}, target ({targetX},{targetY}), {_state.ZoneNPCs.Count} NPCs");

        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            // Check if NPC is at the target position OR adjacent to player
            bool atTarget = (npc.X == targetX && npc.Y == targetY);
            bool adjacent = Math.Abs(npc.X - _state.PlayerX) <= 1 &&
                           Math.Abs(npc.Y - _state.PlayerY) <= 1 &&
                           !(npc.X == _state.PlayerX && npc.Y == _state.PlayerY);

            Console.WriteLine($"  NPC at ({npc.X},{npc.Y}): atTarget={atTarget}, adjacent={adjacent}");

            if (atTarget || adjacent)
            {
                // Hostile NPCs can't be talked to - let the attack handle them
                if (npc.IsHostile)
                {
                    return false;  // Fall through to attack
                }

                // Get NPC name
                string npcName;
                if (npc.CharacterId >= 0 && npc.CharacterId < _gameData!.Characters.Count)
                {
                    var character = _gameData.Characters[npc.CharacterId];
                    npcName = string.IsNullOrEmpty(character.Name) ? "Stranger" : character.Name;
                }
                else
                {
                    // NPC is a tile-based entity
                    npcName = "Stranger";
                }

                // Make NPC face the player
                npc.Direction = GetDirectionToward(npc.X, npc.Y, _state.PlayerX, _state.PlayerY);

                // Store NPC position for action checks
                _state.SetVariable(1, npc.X);
                _state.SetVariable(2, npc.Y);
                _state.SetVariable(3, npc.CharacterId);

                // Set action executor context
                if (_actionExecutor != null)
                {
                    _actionExecutor.InteractingNpcId = npc.CharacterId;
                }

                // Try to execute any NPC-related actions first
                // (This may trigger scripted dialogue via OnDialogue event)
                var hadDialogueBefore = _messages.HasDialogue;
                _actionExecutor?.ExecuteZoneActions(ActionTrigger.NpcTalk);

                // Clear the context after execution
                if (_actionExecutor != null)
                {
                    _actionExecutor.InteractingNpcId = null;
                }

                // If no scripted dialogue was shown, use fallback dialogue
                if (!_messages.HasDialogue || hadDialogueBefore == _messages.HasDialogue)
                {
                    // Special dialogue for Yoda (check for Yoda tile 780)
                    const int YODA_TILE_ID = 780;
                    bool isYoda = npc.CharacterId == YODA_TILE_ID;

                    if (isYoda && _worldGenerator?.CurrentWorld != null)
                    {
                        var mission = _worldGenerator.CurrentWorld.Mission;
                        var world = _worldGenerator.CurrentWorld;

                        Console.WriteLine($"Yoda interaction: StartingItemId={world.StartingItemId}, HasItem={world.StartingItemId.HasValue && _state.HasItem(world.StartingItemId.Value)}, MissionComplete={mission?.IsCompleted}");

                        // Check if mission is complete
                        if (mission != null && mission.IsCompleted)
                        {
                            // Mission complete! Congratulate the player
                            _state.GamesWon++;
                            _state.IsGameWon = true;
                            _messages.ShowDialogue("Yoda", $"Completed the mission, you have! Strong with the Force, you are. {_state.GamesWon} missions completed.");
                            _messages.ShowMessage("YOU WIN! Press R to start a new mission.", MessageType.System);
                            _sounds?.PlaySound(SoundManager.SoundPickup);
                        }
                        // Give the starting item if player doesn't have it yet
                        else if (world.StartingItemId.HasValue && !_state.HasItem(world.StartingItemId.Value))
                        {
                            Console.WriteLine($"Yoda giving item: {world.StartingItemId.Value}");
                            _state.AddItem(world.StartingItemId.Value);
                            var itemName = GetTileName(world.StartingItemId.Value) ?? "an item";

                            // Show mission briefing
                            var briefing = mission != null
                                ? $"A mission for you, I have. {mission.Name}. Take this {itemName}. To {mission.Planet}, you must go. Find your X-Wing nearby."
                                : $"Take this {itemName}. Your journey begins. Find your X-Wing nearby.";
                            _messages.ShowDialogue("Yoda", briefing);
                            _messages.ShowMessage($"Received: {itemName}", MessageType.Pickup);
                            _sounds?.PlaySound(SoundManager.SoundPickup);

                            // Show first objective hint
                            if (mission?.CurrentPuzzleStep != null && !string.IsNullOrEmpty(mission.CurrentPuzzleStep.Hint))
                            {
                                _messages.ShowMessage($"Hint: {mission.CurrentPuzzleStep.Hint}", MessageType.Info);
                            }
                        }
                        else if (mission != null)
                        {
                            // Show current mission status
                            var objective = world.GetCurrentObjective();
                            _messages.ShowDialogue("Yoda", $"To {mission.Planet}, you must go. {objective}. May the Force be with you.");
                        }
                        else
                        {
                            _messages.ShowDialogue("Yoda", "Strong in the Force, you are.");
                        }
                    }
                    // Check if NPC has an item to give (from IZAX data)
                    else if (npc.CarriedItemId.HasValue && !npc.HasGivenItem)
                    {
                        var itemId = npc.CarriedItemId.Value;
                        var itemName = GetTileName(itemId) ?? $"an item";

                        // Give the item to the player
                        _state.AddItem(itemId);
                        npc.HasGivenItem = true;

                        _messages.ShowDialogue(npcName, $"Here, take this {itemName}. You may need it.");
                        _messages.ShowMessage($"Received: {itemName}", MessageType.Pickup);
                        _sounds?.PlaySound(SoundManager.SoundPickup);

                        Console.WriteLine($"NPC {npcName} gave item {itemId} ({itemName}) to player");
                    }
                    else
                    {
                        var dialogues = new[]
                        {
                            "Hello there, traveler!",
                            "May the Force be with you.",
                            "I have nothing to trade right now.",
                            "Be careful out there!",
                            "The Empire has been causing trouble...",
                            "Have you seen any droids around?",
                            "This is a peaceful place.",
                            "Good luck on your journey!"
                        };
                        var dialogue = dialogues[_random.Next(dialogues.Length)];
                        _messages.ShowDialogue(npcName, dialogue);
                    }
                }
                _sounds?.PlaySound(SoundManager.SoundTalk);
                return true;
            }
        }

        return false;
    }

    private void PerformAttack()
    {
        // Determine weapon type
        bool isRangedWeapon = false;
        if (_state.SelectedWeapon.HasValue && _state.SelectedWeapon.Value > 0 &&
            _state.SelectedWeapon.Value < _gameData!.Tiles.Count)
        {
            var weaponTile = _gameData.Tiles[_state.SelectedWeapon.Value];
            isRangedWeapon = (weaponTile.Flags & (TileFlags.WeaponLightBlaster | TileFlags.WeaponHeavyBlaster)) != 0;
        }

        if (isRangedWeapon)
        {
            // Ranged attack - spawn projectile
            PerformRangedAttack();
        }
        else
        {
            // Melee attack
            PerformMeleeAttack();
        }
    }

    private void PerformRangedAttack()
    {
        // Trigger attack animation
        _state.IsAttacking = true;
        _state.AttackTimer = 0.15;  // Shorter for shooting

        // Calculate projectile direction
        double velX = 0, velY = 0;
        const double projectileSpeed = 12.0;  // Tiles per second

        switch (_state.PlayerDirection)
        {
            case Direction.Up: velY = -projectileSpeed; break;
            case Direction.Down: velY = projectileSpeed; break;
            case Direction.Left: velX = -projectileSpeed; break;
            case Direction.Right: velX = projectileSpeed; break;
        }

        // Spawn projectile
        var projectile = new Projectile
        {
            X = _state.PlayerX + (velX > 0 ? 0.5 : velX < 0 ? -0.5 : 0),
            Y = _state.PlayerY + (velY > 0 ? 0.5 : velY < 0 ? -0.5 : 0),
            VelocityX = velX,
            VelocityY = velY,
            Damage = 50,
            LifeTime = 2.0,
            Type = ProjectileType.Blaster
        };
        _state.Projectiles.Add(projectile);

        _sounds?.PlaySound(SoundManager.SoundAttack);
        _messages.ShowMessage("*pew*", MessageType.Combat);
    }

    private void PerformMeleeAttack()
    {
        // Trigger attack animation
        _state.IsAttacking = true;
        _state.AttackTimer = 0.3;

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

        Console.WriteLine($"Melee attack at ({targetX},{targetY}), {_state.ZoneNPCs.Count} NPCs in zone");

        // Check for NPC at or near target position (melee has some range)
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            // Calculate distance to NPC
            var distX = Math.Abs(npc.X - targetX);
            var distY = Math.Abs(npc.Y - targetY);
            var dist = distX + distY;

            Console.WriteLine($"  NPC at ({npc.X},{npc.Y}), dist={dist}");

            // Hit if within melee range (1 tile from target, 2 tiles from player)
            if (dist <= 1)
            {
                // Calculate damage
                int damage = _state.SelectedWeapon.HasValue ? 50 : 25;

                bool killed = npc.TakeDamage(damage);
                _state.AttackFlashTimer = 0.5;
                _sounds?.PlaySound(SoundManager.SoundAttack);

                // Get NPC name for message
                var npcName = GetCharacterName(npc.CharacterId) ?? $"Target";

                if (killed)
                {
                    _messages.ShowCombat($"{npcName} defeated!");
                    _sounds?.PlaySound(SoundManager.SoundDeath);
                    Console.WriteLine($"  Killed NPC!");
                }
                else
                {
                    _messages.ShowCombat($"Hit! {npc.Health} HP left");
                    Console.WriteLine($"  Hit NPC for {damage} damage, {npc.Health} HP left");
                }

                _actionExecutor?.ExecuteZoneActions(ActionTrigger.Attack);
                return;
            }
        }

        // No NPC found - show swing/miss feedback
        _state.AttackFlashTimer = 0.2;
        _messages.ShowMessage("*swing*", MessageType.Combat);
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
    /// Initializes NPCs from zone objects and IZAX entity data.
    /// </summary>
    private void InitializeZoneNPCs()
    {
        _state.ZoneNPCs.Clear();

        if (_state.CurrentZone == null)
            return;

        // 1. Spawn NPCs from zone objects (PuzzleNPC)
        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.PuzzleNPC)
            {
                var npc = NPC.FromZoneObject(obj);
                ConfigureNPCFromCharacter(npc);
                _state.ZoneNPCs.Add(npc);
            }
        }

        // 2. Spawn NPCs from IZAX entity data
        if (_state.CurrentZone.AuxData?.Entities != null)
        {
            foreach (var entity in _state.CurrentZone.AuxData.Entities)
            {
                // Skip invalid entries
                if (entity.CharacterId == 0xFFFF)
                    continue;

                var npc = new NPC
                {
                    CharacterId = entity.CharacterId,
                    X = entity.X,
                    Y = entity.Y,
                    StartX = entity.X,
                    StartY = entity.Y,
                    Direction = Direction.Down,
                    IsEnabled = true,
                    // Store the item this NPC will give when interacted with
                    CarriedItemId = entity.ItemTileId > 0 && entity.ItemTileId != 0xFFFF ? entity.ItemTileId : null,
                    CarriedItemQuantity = entity.ItemQuantity
                };

                ConfigureNPCFromCharacter(npc);

                // Log IZAX NPC spawn
                var npcName = GetCharacterName(npc.CharacterId) ?? $"TileID #{npc.CharacterId}";
                var itemInfo = npc.CarriedItemId.HasValue ? $", carries item {npc.CarriedItemId}" : "";
                Console.WriteLine($"  IZAX NPC: {npcName} at ({npc.X},{npc.Y}){itemInfo}");

                _state.ZoneNPCs.Add(npc);
            }
        }

        // Count items in zone
        int itemCount = 0;
        foreach (var obj in _state.CurrentZone.Objects)
        {
            if ((obj.Type == ZoneObjectType.CrateItem || obj.Type == ZoneObjectType.CrateWeapon) &&
                !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
            {
                itemCount++;
            }
        }

        // Show zone summary if there's interesting content
        if (_state.ZoneNPCs.Count > 0 || itemCount > 0)
        {
            var parts = new List<string>();
            if (_state.ZoneNPCs.Count > 0)
                parts.Add($"{_state.ZoneNPCs.Count} NPC(s)");
            if (itemCount > 0)
                parts.Add($"{itemCount} item(s)");
            _messages.ShowMessage($"Zone {_state.CurrentZoneId}: {string.Join(", ", parts)}", MessageType.Info);
        }
    }

    /// <summary>
    /// Configures an NPC based on character data.
    /// </summary>
    private void ConfigureNPCFromCharacter(NPC npc)
    {
        // Set NPC properties based on character type if it's a valid character
        if (npc.CharacterId < _gameData!.Characters.Count)
        {
            var character = _gameData.Characters[npc.CharacterId];
            var charName = string.IsNullOrEmpty(character.Name) ? $"Character #{npc.CharacterId}" : character.Name;

            // Enemy characters are hostile and chase the player
            if (character.Type == CharacterType.Enemy)
            {
                npc.IsHostile = true;
                npc.Behavior = NPCBehavior.Chasing;
                npc.MoveCooldown = 0.4;  // Enemies move faster
                Console.WriteLine($"  NPC: {charName} (Enemy) at ({npc.X},{npc.Y})");
            }
            // Friendly NPCs just wander
            else if (character.Type == CharacterType.Friendly)
            {
                npc.Behavior = NPCBehavior.Wandering;
                npc.MoveCooldown = 0.8;  // Friendlies move slower
                Console.WriteLine($"  NPC: {charName} (Friendly) at ({npc.X},{npc.Y})");
            }
            else
            {
                Console.WriteLine($"  NPC: {charName} ({character.Type}) at ({npc.X},{npc.Y})");
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
        else
        {
            // CharacterId is actually a tile ID, not a character reference
            // Check if it's a friendly character tile (flag 0x40000)
            if (npc.CharacterId < _gameData.Tiles.Count)
            {
                var tile = _gameData.Tiles[npc.CharacterId];
                if (tile.IsCharacter)
                {
                    // Check character type flags in tile
                    var flags = (int)tile.Flags;
                    if ((flags & 0x40000) != 0)  // CharFriendly
                    {
                        npc.IsHostile = false;
                        npc.Behavior = NPCBehavior.Stationary;
                        Console.WriteLine($"  NPC: Friendly TileID #{npc.CharacterId} at ({npc.X},{npc.Y})");
                    }
                    else if ((flags & 0x20000) != 0)  // CharEnemy
                    {
                        npc.IsHostile = true;
                        npc.Behavior = NPCBehavior.Chasing;
                        Console.WriteLine($"  NPC: Enemy TileID #{npc.CharacterId} at ({npc.X},{npc.Y})");
                    }
                    else
                    {
                        Console.WriteLine($"  NPC: TileID #{npc.CharacterId} at ({npc.X},{npc.Y})");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Spawns Yoda as an NPC in Dagobah zones.
    /// </summary>
    private void SpawnYodaIfNeeded(int zoneId)
    {
        if (_worldGenerator?.CurrentWorld == null) return;

        // Check if this is a Dagobah zone (Yoda's home)
        if (_worldGenerator.CurrentWorld.DagobahZones.Contains(zoneId))
        {
            // Mark that this is Dagobah
            _state.SetVariable(998, 1);
            Console.WriteLine($"  Dagobah zone - checking for Yoda");

            // Spawn Yoda if this is the designated Yoda zone
            if (zoneId == _worldGenerator.CurrentWorld.YodaZoneId)
            {
                var yodaPos = _worldGenerator.CurrentWorld.YodaPosition;

                // Find a walkable position near the intended spawn point
                var zone = _state.CurrentZone;
                if (zone != null)
                {
                    // Try to find a walkable spot near spawn location
                    var (finalX, finalY) = FindWalkablePosition(zone, yodaPos.x, yodaPos.y);
                    Console.WriteLine($"  Spawning Yoda at ({finalX}, {finalY}) (intended: {yodaPos.x}, {yodaPos.y})");

                    // Create Yoda as a friendly NPC
                    // Yoda uses tile 780 - a FRIENDLY character tile not in the CHAR list
                    // (Found by searching for Character tiles with CharFriendly flag 0x40000)
                    const ushort YODA_TILE_ID = 780;

                    var yoda = new NPC
                    {
                        CharacterId = YODA_TILE_ID,
                        X = finalX,
                        Y = finalY,
                        StartX = finalX,
                        StartY = finalY,
                        Direction = Direction.Down,
                        Health = 999,
                        MaxHealth = 999,
                        IsEnabled = true,
                        IsHostile = false,
                        Behavior = NPCBehavior.Stationary,
                        Damage = 0
                    };
                    _state.ZoneNPCs.Add(yoda);
                }
            }
        }
        else
        {
            _state.SetVariable(998, 0);
        }
    }

    /// <summary>
    /// Finds a walkable position near the target coordinates.
    /// </summary>
    private (int x, int y) FindWalkablePosition(Zone zone, int targetX, int targetY)
    {
        // Check if target is walkable
        if (IsPositionWalkable(zone, targetX, targetY))
            return (targetX, targetY);

        // Search in expanding squares around target
        for (int radius = 1; radius <= 5; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue; // Only check perimeter

                    int x = targetX + dx;
                    int y = targetY + dy;

                    if (IsPositionWalkable(zone, x, y))
                        return (x, y);
                }
            }
        }

        // Fallback: use spawn location from zone
        foreach (var obj in zone.Objects)
        {
            if (obj.Type == ZoneObjectType.SpawnLocation)
                return (obj.X, obj.Y);
        }

        return (targetX, targetY); // Last resort
    }

    private bool IsPositionWalkable(Zone zone, int x, int y)
    {
        if (x < 1 || x >= zone.Width - 1 || y < 1 || y >= zone.Height - 1)
            return false;

        var middleTile = zone.GetTile(x, y, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            if (tile.IsObject && !tile.IsFloor)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks for X-Wing and spawns it as a visible object.
    /// </summary>
    private void CheckForXWing(int zoneId)
    {
        if (_worldGenerator?.CurrentWorld == null) return;

        // X-Wing is available in all Dagobah zones
        if (_worldGenerator.CurrentWorld.DagobahZones.Contains(zoneId))
        {
            _state.SetVariable(999, 1);  // X-Wing available
            Console.WriteLine($"  X-Wing available (press X anywhere to travel)");

            // Spawn X-Wing as an NPC so it renders like other characters
            if (_state.CurrentZone != null)
            {
                foreach (var obj in _state.CurrentZone.Objects)
                {
                    if (obj.Type == ZoneObjectType.XWingFromDagobah)
                    {
                        Console.WriteLine($"  X-Wing location at ({obj.X}, {obj.Y})");
                        // Store X-Wing position for rendering
                        _state.XWingPosition = (obj.X, obj.Y);
                        break;
                    }
                }
            }
        }
        else
        {
            _state.SetVariable(999, 0);
        }
    }

    /// <summary>
    /// Handles X-Wing travel between Dagobah and mission planet.
    /// </summary>
    private void TravelToPlanet()
    {
        if (_worldGenerator?.CurrentWorld == null) return;

        var world = _worldGenerator.CurrentWorld;
        bool onDagobah = world.DagobahZones.Contains(_state.CurrentZoneId);

        if (onDagobah)
        {
            // Travel FROM Dagobah TO mission planet
            var landingZoneId = world.LandingZoneId;
            if (landingZoneId > 0 && landingZoneId < _gameData!.Zones.Count)
            {
                var destZone = _gameData.Zones[landingZoneId];
                _messages.ShowMessage($"Traveling to {world.Planet}...", MessageType.System);
                _sounds?.PlaySound(SoundManager.SoundDoor);

                // Find spawn point
                int spawnX = destZone.Width / 2;
                int spawnY = destZone.Height / 2;
                foreach (var obj in destZone.Objects)
                {
                    if (obj.Type == ZoneObjectType.SpawnLocation ||
                        obj.Type == ZoneObjectType.XWingToDagobah)
                    {
                        spawnX = obj.X;
                        spawnY = obj.Y;
                        break;
                    }
                }

                LoadZone(landingZoneId, spawnX, spawnY);
            }
            else
            {
                _messages.ShowMessage("Talk to Yoda first to receive your mission.", MessageType.Info);
            }
        }
        else
        {
            // Travel FROM mission planet BACK TO Dagobah
            var dagobahZoneId = world.StartingZoneId;
            if (dagobahZoneId > 0 && dagobahZoneId < _gameData!.Zones.Count)
            {
                var destZone = _gameData.Zones[dagobahZoneId];
                _messages.ShowMessage("Returning to Dagobah...", MessageType.System);
                _sounds?.PlaySound(SoundManager.SoundDoor);

                // Find spawn point near X-Wing
                int spawnX = destZone.Width / 2;
                int spawnY = destZone.Height / 2;
                foreach (var obj in destZone.Objects)
                {
                    if (obj.Type == ZoneObjectType.XWingFromDagobah)
                    {
                        spawnX = obj.X;
                        spawnY = obj.Y + 1;  // Spawn below X-Wing
                        break;
                    }
                    if (obj.Type == ZoneObjectType.SpawnLocation)
                    {
                        spawnX = obj.X;
                        spawnY = obj.Y;
                    }
                }

                LoadZone(dagobahZoneId, spawnX, spawnY);
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

        // Update projectiles
        UpdateProjectiles(deltaTime);

        // Update bot AI
        if (_bot?.IsRunning == true)
            _bot.Update(deltaTime);

        // Decay visual effect timers
        if (_state.DamageFlashTimer > 0)
            _state.DamageFlashTimer = Math.Max(0, _state.DamageFlashTimer - deltaTime * 4);
        if (_state.AttackFlashTimer > 0)
            _state.AttackFlashTimer = Math.Max(0, _state.AttackFlashTimer - deltaTime * 6);

        // Update attack animation timer
        if (_state.IsAttacking)
        {
            _state.AttackTimer -= deltaTime;
            if (_state.AttackTimer <= 0)
            {
                _state.IsAttacking = false;
                _state.AttackTimer = 0;
            }
        }

        // Update messages
        _messages.Update(deltaTime);

        // Check for game over conditions
        if (_state.Health <= 0 && !_state.IsGameOver)
        {
            _state.IsGameOver = true;
            _messages.ShowMessage("GAME OVER - Press R to restart", MessageType.System);
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
                        _state.DamageFlashTimer = 1.0;  // Trigger damage flash
                        _sounds?.PlaySound(SoundManager.SoundHurt);

                        var attackerName = GetCharacterName(npc.CharacterId) ?? "Enemy";
                        _messages.ShowCombat($"{attackerName} hits you! (-{npc.Damage} HP)");
                    }
                }
            }
        }
    }

    private void UpdateProjectiles(double deltaTime)
    {
        for (int i = _state.Projectiles.Count - 1; i >= 0; i--)
        {
            var projectile = _state.Projectiles[i];
            projectile.Update(deltaTime);

            // Check if out of bounds
            var (tileX, tileY) = projectile.TilePosition;
            if (tileX < 0 || tileX >= _state.CurrentZone!.Width ||
                tileY < 0 || tileY >= _state.CurrentZone.Height)
            {
                projectile.IsActive = false;
            }

            // Check for wall collision
            if (projectile.IsActive)
            {
                var middleTile = _state.CurrentZone.GetTile(tileX, tileY, 1);
                if (middleTile != 0xFFFF && middleTile < _gameData!.Tiles.Count)
                {
                    var tile = _gameData.Tiles[middleTile];
                    if (tile.IsObject && !tile.IsDraggable)
                    {
                        projectile.IsActive = false;
                    }
                }
            }

            // Check for NPC collision
            if (projectile.IsActive)
            {
                foreach (var npc in _state.ZoneNPCs)
                {
                    if (!npc.IsEnabled || !npc.IsAlive)
                        continue;

                    if (npc.X == tileX && npc.Y == tileY)
                    {
                        bool killed = npc.TakeDamage(projectile.Damage);
                        projectile.IsActive = false;
                        _state.AttackFlashTimer = 0.3;

                        var npcName = GetCharacterName(npc.CharacterId) ?? "Target";
                        if (killed)
                        {
                            _messages.ShowCombat($"{npcName} defeated!");
                            _sounds?.PlaySound(SoundManager.SoundDeath);
                        }
                        else
                        {
                            _messages.ShowCombat($"Hit! {npc.Health} HP left");
                        }
                        break;
                    }
                }
            }

            // Remove inactive projectiles
            if (!projectile.IsActive)
            {
                _state.Projectiles.RemoveAt(i);
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

        // Render X-Wing if in Dagobah zone
        RenderXWing();

        // Render zone items (crates, weapons on ground)
        RenderZoneItems();

        // Render NPCs
        RenderNPCs();

        // Render player character
        RenderPlayer();

        // Render attack animation if attacking
        if (_state.IsAttacking && _state.SelectedWeapon.HasValue)
        {
            var playerScreenX = (_state.PlayerX - _state.CameraX) * Data.Tile.Width * GameRenderer.Scale;
            var playerScreenY = (_state.PlayerY - _state.CameraY) * Data.Tile.Height * GameRenderer.Scale;
            var direction = (int)_state.PlayerDirection;
            var progress = _state.AttackTimer / 0.3;  // Normalized progress

            // Get weapon animation tile based on weapon and direction
            // Light Saber is Character 14, other weapons are Characters 9-13
            int weaponCharIndex = GetWeaponCharacterIndex(_state.SelectedWeapon.Value);
            int weaponTile = GetWeaponTileForDirection(weaponCharIndex, _state.PlayerDirection);

            if (weaponTile > 0)
            {
                _renderer.RenderWeaponAttack(weaponTile, playerScreenX, playerScreenY, direction, progress);
            }
            else
            {
                _renderer.RenderMeleeSlash(playerScreenX, playerScreenY, direction, progress);
            }
        }
        else if (_state.IsAttacking)
        {
            // Unarmed attack
            var playerScreenX = (_state.PlayerX - _state.CameraX) * Data.Tile.Width * GameRenderer.Scale;
            var playerScreenY = (_state.PlayerY - _state.CameraY) * Data.Tile.Height * GameRenderer.Scale;
            _renderer.RenderMeleeSlash(playerScreenX, playerScreenY, (int)_state.PlayerDirection, _state.AttackTimer / 0.3);
        }

        // Render projectiles
        RenderProjectiles();

        // Render HUD
        _renderer.RenderHUD(_state.Health, _state.MaxHealth, _state.Inventory, _state.SelectedWeapon, _state.SelectedItem);

        // Render zone info
        _renderer.RenderZoneInfo(
            _state.CurrentZoneId,
            _state.CurrentZone.Planet.ToString(),
            _state.CurrentZone.Width,
            _state.CurrentZone.Height);

        // Render bot status if running
        if (_bot?.IsRunning == true)
        {
            _renderer.RenderBotStatus(_bot.CurrentTask);
        }

        // Render visual feedback overlays
        if (_state.DamageFlashTimer > 0)
            _renderer.RenderDamageOverlay(_state.DamageFlashTimer);
        if (_state.AttackFlashTimer > 0)
            _renderer.RenderAttackOverlay(_state.AttackFlashTimer);

        // Render messages
        _renderer.RenderMessages(_messages.GetMessages(), _messages.CurrentDialogue);

        // Present frame
        _renderer.Present();
    }

    /// <summary>
    /// Renders the X-Wing at its position in the starting zone only.
    /// </summary>
    private void RenderXWing()
    {
        if (_renderer == null || _state.XWingPosition == null || _worldGenerator == null)
            return;

        // Only render X-Wing in the starting zone
        if (_state.CurrentZoneId != _worldGenerator.CurrentWorld.XWingZoneId)
            return;

        var (xwingX, xwingY) = _state.XWingPosition.Value;

        // Check if X-Wing is within viewport
        if (xwingX < _state.CameraX - 2 || xwingX >= _state.CameraX + GameRenderer.ViewportTilesX + 2 ||
            xwingY < _state.CameraY - 2 || xwingY >= _state.CameraY + GameRenderer.ViewportTilesY + 2)
            return;

        // X-Wing as 2x2 grid: 948, 949 on top row, 950, 951 on bottom row
        _renderer.RenderSprite(948, xwingX, xwingY, _state.CameraX, _state.CameraY);
        _renderer.RenderSprite(949, xwingX + 1, xwingY, _state.CameraX, _state.CameraY);
        _renderer.RenderSprite(950, xwingX, xwingY + 1, _state.CameraX, _state.CameraY);
        _renderer.RenderSprite(951, xwingX + 1, xwingY + 1, _state.CameraX, _state.CameraY);
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

    private void RenderProjectiles()
    {
        foreach (var projectile in _state.Projectiles)
        {
            if (!projectile.IsActive)
                continue;

            var (tileX, tileY) = projectile.TilePosition;

            // Check if within viewport
            if (tileX < _state.CameraX || tileX >= _state.CameraX + GameRenderer.ViewportTilesX ||
                tileY < _state.CameraY || tileY >= _state.CameraY + GameRenderer.ViewportTilesY)
                continue;

            // Render projectile
            var screenX = (int)((projectile.X - _state.CameraX) * Data.Tile.Width * GameRenderer.Scale);
            var screenY = (int)((projectile.Y - _state.CameraY) * Data.Tile.Height * GameRenderer.Scale);
            _renderer!.RenderProjectile(screenX, screenY, projectile.Type);
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

    /// <summary>
    /// Gets a tile name by ID, if available.
    /// </summary>
    private string? GetTileName(int tileId)
    {
        if (_gameData!.TileNames.TryGetValue(tileId, out var name))
            return name;
        return null;
    }

    /// <summary>
    /// Checks if a tile is a locator/R2D2 item.
    /// </summary>
    private bool IsLocatorTile(int tileId)
    {
        // Check by tile name
        var name = GetTileName(tileId);
        if (name != null)
        {
            var nameLower = name.ToLowerInvariant();
            if (nameLower.Contains("r2") || nameLower.Contains("locator") || nameLower.Contains("droid"))
                return true;
        }
        // Check by flags
        if (tileId < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[tileId];
            if ((tile.Flags & TileFlags.ItemLocator) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shows a hint from the locator/R2D2.
    /// </summary>
    private void ShowLocatorHint()
    {
        if (_worldGenerator?.CurrentWorld == null)
        {
            _messages.ShowDialogue("R2-D2", "*beep boop* No mission data available.");
            _sounds?.PlaySound(SoundManager.SoundTalk);
            return;
        }

        var mission = _worldGenerator.CurrentWorld.Mission;
        if (mission == null)
        {
            _messages.ShowDialogue("R2-D2", "*beep boop* Explore the area to find clues.");
            _sounds?.PlaySound(SoundManager.SoundTalk);
            return;
        }

        if (mission.IsCompleted)
        {
            _messages.ShowDialogue("R2-D2", "*happy beeps* Mission complete! Return to Yoda on Dagobah.");
            _sounds?.PlaySound(SoundManager.SoundTalk);
            return;
        }

        // Get current objective hint
        var step = mission.CurrentPuzzleStep;
        if (step != null && !string.IsNullOrEmpty(step.Hint))
        {
            _messages.ShowDialogue("R2-D2", $"*beep boop* {step.Hint}");
        }
        else
        {
            var objective = _worldGenerator.CurrentWorld.GetCurrentObjective();
            _messages.ShowDialogue("R2-D2", $"*beep boop* {objective}");
        }
        _sounds?.PlaySound(SoundManager.SoundTalk);
    }

    /// <summary>
    /// Gets the character index for a weapon tile.
    /// </summary>
    private int GetWeaponCharacterIndex(int weaponTileId)
    {
        // Map weapon tile IDs to character indices
        // These are approximate - weapons are stored as characters 9-14
        if (weaponTileId == 510) return 14;  // Lightsaber -> Character 14 "Light Saber"
        return -1;
    }

    /// <summary>
    /// Gets the weapon attack tile for a direction.
    /// Luke's lightsaber attack frames at atlas 241x1472 = tile 2122+
    /// </summary>
    private int GetWeaponTileForDirection(int weaponCharIndex, Direction direction)
    {
        if (weaponCharIndex == 14)  // Lightsaber
        {
            // Luke's lightsaber attack tiles from atlas 241x1472 = tile 2122
            // Sequential tiles: 2122, 2123 for up/down and left/right
            return direction switch
            {
                Direction.Up => 2122,
                Direction.Down => 2123,
                Direction.Right => 2124,
                Direction.Left => 2125,
                _ => 2122
            };
        }
        return -1;
    }

    /// <summary>
    /// Gets a character name by ID.
    /// </summary>
    private string? GetCharacterName(int characterId)
    {
        if (characterId >= 0 && characterId < _gameData!.Characters.Count)
        {
            var name = _gameData.Characters[characterId].Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        return null;
    }

    public void Dispose()
    {
        // Clean up controller
        if (_controller != null)
        {
            SDL.GameControllerClose(_controller);
            _controller = null;
        }

        _sounds?.Dispose();
        _renderer?.Dispose();
    }
}
