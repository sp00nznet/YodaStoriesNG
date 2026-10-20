using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Audio;
using YodaStoriesNG.Engine.Bot;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Debug;
using YodaStoriesNG.Engine.Parsing;
using YodaStoriesNG.Engine.Rendering;
using YodaStoriesNG.Engine.UI;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Main game engine that coordinates all systems.
/// </summary>
public unsafe partial class GameEngine : IDisposable
{
    private GameData? _gameData;
    private GameState _state;
    private GameRenderer? _renderer;
    private ActionExecutor? _actionExecutor;
    private WorldGenerator? _worldGenerator;
    private MessageSystem _messages = new();
    private SoundManager? _sounds;
    private MissionBot? _bot;
    private DebugTools? _debugTools;
    private DebugOverlay? _debugOverlay;
    private DebugMapWindow? _debugMapWindow;
    private ScriptEditorWindow? _scriptViewer;
    private AssetViewerWindow? _assetViewer;
    private SaveGameEditor? _saveInspector;
    private ZoneEditorWindow? _zoneEditor;
    private ControlsWindow? _controlsWindow;
    private AboutWindow? _aboutWindow;
    private ScoreWindow? _scoreWindow;
    private UI.HighScoreWindow? _highScoreWindow;
    private TitleScreen? _titleScreen;
    private UI.IMenuBar? _menuBar;
    private UI.ControllerOverlay? _controllerOverlay;
    private SectorPuzzles? _sectorPuzzles;  // worlds from OriginalWorldGenerator only

    private bool _isRunning;
    private readonly string _dataPath;
    private bool _showingTitleScreen = true;
    private WorldSize _selectedWorldSize = WorldSize.Medium;
    private int _graphicsScale = 2;  // 2x or 4x

    // Controller support
    private SDLGameController* _controller;
    private bool _controllerConnected;
    private const int ControllerDeadzone = 8000;  // Analog stick deadzone

    // Timing
    private const double TargetFrameTime = 1.0 / 60.0; // 60 FPS
    private const double AnimationFrameTime = 0.15; // 150ms per animation frame
    private double _controllerMoveTimer = 0;  // Rate limit controller movement
    private double _playerWalkingTimer;
    private double _playerAnimationTimer;
    private int _playerAnimationFrame;
    private double _attackAnimationDuration = 0.3;

    // Drag and drop state
    private bool _isDragging = false;
    private int? _draggedItemId = null;
    private int? _draggedFromSlot = null;  // Inventory slot index, or -1 for weapon slot
    private int? _draggedFromZone;
    private int _dragStartX, _dragStartY;
    private int _dragCurrentX, _dragCurrentY;

    /// <summary>
    /// Whether the bot is currently running.
    /// </summary>
    public bool IsBotRunning => _bot?.IsRunning ?? false;

    // Yoda Stories weapon tile constants for 15-mission progression
    private const int TILE_BASIC_LIGHTSABER = 18;      // Starting weapon
    private const int TILE_UPGRADED_LIGHTSABER = 510;  // After 5 missions
    private const int TILE_THE_FORCE = 511;            // After 10 missions (ranged)

    // Indiana Jones weapon tiles: each weapon character's inventory tile (CHAR frame 1).
    // The pistol is not a reward for games won - world generation hides it in a Find zone.
    private const int TILE_WHIP = 449;

    /// <summary>
    /// Gets the appropriate weapon for the player's mission count.
    /// </summary>
    private int GetWeaponForMissionCount(int missionsCompleted)
    {
        if (_gameData?.GameType == GameType.IndianaJones)
        {
            return TILE_WHIP;
        }
        else
        {
            if (missionsCompleted >= 10) return TILE_THE_FORCE;
            if (missionsCompleted >= 5) return TILE_UPGRADED_LIGHTSABER;
            return TILE_BASIC_LIGHTSABER;
        }
    }

    /// <summary>
    /// Upgrades the player's weapon, replacing the old one.
    /// </summary>
    private void UpgradeWeapon(int newWeaponTile)
    {
        // Remove old weapon copies from the inventory as well as the cycling list.
        var replacedWeapons = _state.Weapons.ToHashSet();
        _state.Inventory.RemoveAll(replacedWeapons.Contains);
        _state.Weapons.Clear();
        _state.WeaponAmmo.Clear();
        _state.Weapons.Add(newWeaponTile);
        _state.CurrentWeaponIndex = 0;
        _state.SelectedWeapon = newWeaponTile;
        _state.SelectedItem = null;

        // Initialize ammo for the new weapon
        InitializeWeaponAmmo(newWeaponTile);
        Console.WriteLine($"[Weapon] Upgraded to tile {newWeaponTile}");
    }

    /// <summary>
    /// Gets weapon configuration (ammo, damage) based on tile ID and flags.
    /// </summary>
    private (int maxAmmo, int damage, bool isSingleUse) GetWeaponConfig(int tileId)
    {
        // Damage values scaled for 768 HP system (3 lives x 256 HP)
        // Check if it's The Force - unlimited ammo, stun duration
        if (tileId == TILE_THE_FORCE)
            return (-1, 10, false);  // -1 = unlimited, damage = stun duration in ticks

        // Lightsabers - melee, no ammo
        if (tileId == TILE_BASIC_LIGHTSABER || tileId == TILE_UPGRADED_LIGHTSABER)
            return (-1, 200, false);

        // Indy's whip is his lightsaber: melee, never runs dry
        if (tileId == TILE_WHIP && _gameData?.GameType == GameType.IndianaJones)
            return (-1, 200, false);

        // Check tile flags for weapon type
        if (tileId < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[tileId];
            var flags = tile.Flags;

            // Heavy blaster - more damage, less ammo
            if ((flags & TileFlags.WeaponHeavyBlaster) != 0)
                return (15, 300, false);

            // Light blaster - standard
            if ((flags & TileFlags.WeaponLightBlaster) != 0)
                return (30, 200, false);

            // Generic weapon flag (pistols, etc)
            if ((flags & TileFlags.Weapon) != 0)
            {
                // Check for grenade-like items (single use)
                var name = GetTileName(tileId).ToLower();
                if (name.Contains("grenade") || name.Contains("bomb") || name.Contains("thermal"))
                    return (1, 400, true);  // Single use, high damage

                // Default ranged weapon
                return (20, 200, false);
            }
        }

        // Default for unknown weapons
        return (20, 200, false);
    }


    /// <summary>
    /// Initializes ammo tracking for a weapon.
    /// </summary>
    private void InitializeWeaponAmmo(int tileId)
    {
        var (maxAmmo, damage, isSingleUse) = GetWeaponConfig(tileId);

        // -1 means unlimited (melee or The Force)
        if (maxAmmo > 0)
        {
            _state.InitializeWeaponAmmo(tileId, maxAmmo, damage, isSingleUse);
            Console.WriteLine($"[Weapon] Initialized ammo for tile {tileId}: {maxAmmo} shots, {damage} damage" +
                (isSingleUse ? " (single use)" : ""));
        }
    }

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

        // Try to find the data file (support both Yoda Stories and Indiana Jones)
        string? dataFilePath = null;

        // Check for Yoda Stories first
        var yodaPath = Data.GameDataFile.Find(_dataPath, "yodesk.dta");
        var indyPath = Data.GameDataFile.Find(_dataPath, "desktop.daw");

        if (yodaPath != null)
        {
            dataFilePath = yodaPath;
        }
        else if (indyPath != null)
        {
            dataFilePath = indyPath;
        }
        else
        {
            Console.WriteLine($"No game data file found in {_dataPath}");
            Console.WriteLine("Looking for: YODESK.DTA (Yoda Stories) or DESKTOP.DAW (Indiana Jones)");
            Console.WriteLine("Please select a game data file...");

            // Show file picker
            var selectedFile = UI.FileDialogHelper.ShowOpenDataFileDialog(_dataPath);
            if (!string.IsNullOrEmpty(selectedFile) && File.Exists(selectedFile))
            {
                dataFilePath = selectedFile;
                Console.WriteLine($"Selected: {dataFilePath}");
            }
            else
            {
                Console.WriteLine("No file selected. Cannot start without game data.");
                return false;
            }
        }

        var parser = new DtaParser();
        _gameData = parser.Parse(dataFilePath);

        // Set palette animation for game type (different games have different cycling color ranges)
        Palette.SetGameType(_gameData.GameType);

        // Determine window title based on game type
        string windowTitle = _gameData.GameType == GameType.IndianaJones
            ? "Indiana Jones Desktop Adventures NG"
            : "Yoda Stories NG";

        Console.WriteLine($"Game: {(_gameData.GameType == GameType.IndianaJones ? "Indiana Jones" : "Yoda Stories")}");
        Console.WriteLine($"Version: {_gameData.Version}");
        Console.WriteLine($"Loaded: {_gameData.Tiles.Count} tiles, {_gameData.Zones.Count} zones, {_gameData.Characters.Count} characters");

        // Initialize renderer
        _renderer = new GameRenderer(_gameData);
        if (!_renderer.Initialize(windowTitle))
        {
            Console.WriteLine("Failed to initialize renderer");
            return false;
        }

        // Initialize action executor
        _actionExecutor = new ActionExecutor(_gameData, _state);
        _actionExecutor.OnHeroRelocated += CancelSmoothMovement;
        _actionExecutor.IsDialogueActive = () => _messages.HasDialogue;
        _actionExecutor.OnDialogue += (speaker, text) => _messages.ShowDialogue(speaker, text);
        _actionExecutor.OnItemAdded += RegisterScriptedItem;

        // Wire script events once; SuppressDialogue handles initialization.
        _actionExecutor.OnMessage += (text) => _messages.ShowMessage(text, MessageType.Info);
        _actionExecutor.OnPlaySound += (soundId) => _sounds?.PlaySound(soundId);
        _actionExecutor.OnStopSound += () => _sounds?.StopAllSounds();

        LoadSounds(_dataPath);

        // Initialize game controller support
        InitializeController();

        // Initialize UI components
        _titleScreen = new TitleScreen(_renderer.GetFont(), _gameData.StartupScreen, _gameData.Tiles, _gameData.GameType);
        _titleScreen.SetRenderer(_renderer.GetRenderer());
        _titleScreen.OnStartGame += () => { _showingTitleScreen = false; StartNewGame(); };

        // Windows gets the real OS menu; everywhere else user32.dll does not exist, so the
        // SDL one draws into the strip GameRenderer reserves above the game area.
        if (OperatingSystem.IsWindows())
        {
            var nativeMenuBar = new NativeMenuBar();
            nativeMenuBar.Initialize(_renderer.GetWindow());
            nativeMenuBar.OnMenuActivity += () => { _discardMovementTime = true; _suppressHeldMovement = true; };
            _menuBar = nativeMenuBar;
        }
        else
        {
            var menuBar = new UI.MenuBar(_renderer.GetFont());
            menuBar.SetRenderer(_renderer.GetRenderer(), _renderer.GetWindowID());
            _menuBar = menuBar;
        }

        _menuBar.OnNewGame += (size) => { _selectedWorldSize = size; StartNewGame(); };
        _menuBar.OnSaveGame += SaveGame;
        _menuBar.OnSaveGameAs += SaveGameAs;
        _menuBar.OnLoadGame += LoadGame;
        _menuBar.OnExit += () => _isRunning = false;
        _menuBar.OnAssetViewer += () => _assetViewer?.Toggle();
        _menuBar.OnScriptEditor += () => _scriptViewer?.Toggle();
        _menuBar.OnMapViewer += () => _debugMapWindow?.Toggle();
        _menuBar.OnSaveInspector += () => _saveInspector?.Toggle();
        _menuBar.OnZoneEditor += () => _zoneEditor?.Toggle();
        _menuBar.OnEnableBot += EnableBot;
        _menuBar.OnDisableBot += DisableBot;
        _menuBar.OnSetScale += SetGraphicsScale;
        _movementBindings = KeyBindings.Load();
        _smoothMovement = _smoothMovementRequested = _movementBindings.SmoothMovement;
        _menuBar.SmoothMovement = _smoothMovementRequested;
        _menuBar.OnToggleSmoothMovement += () => SetSmoothMovement(!_smoothMovementRequested);
        _menuBar.OnShowKeyboardControls += ShowKeyboardControls;
        _menuBar.OnShowControllerControls += ShowControllerControls;
        _menuBar.OnSelectDataFile += SelectDataFile;
        _menuBar.OnShowAbout += ShowAboutDialog;
        _menuBar.OnShowHighScores += ShowHighScores;
        _menuBar.OnHideMenuBar += () => SetMenuBarVisible(false);
        if (KeyBindings.Load().HideMenuBar)
            SetMenuBarVisible(false);

        // Initialize controls window, about window, and score window
        _controlsWindow = new ControlsWindow();
        _aboutWindow = new AboutWindow();
        _scoreWindow = new ScoreWindow();
        _highScoreWindow = new UI.HighScoreWindow();

        _controllerOverlay = new UI.ControllerOverlay(_renderer.GetFont(), new UI.ControllerOverlay.Item[]
        {
            new(() => "Resume", () => { }),
            new(() => "Save Game", SaveGame),
            new(() => "Load Game", LoadGame),
            new(() => $"New Game ({_selectedWorldSize})", StartNewGame),
            new(() => $"World Size: {_selectedWorldSize}",
                () => _selectedWorldSize = _selectedWorldSize == WorldSize.XtraLarge ? WorldSize.Small : _selectedWorldSize + 1,
                KeepOpen: true),
            new(() => $"Graphics Scale: {_graphicsScale}x",
                () => SetGraphicsScale(_graphicsScale switch { 1 => 2, 2 => 4, _ => 1 }), KeepOpen: true),
            new(() => $"Smooth Movement: {(_smoothMovementRequested ? "On" : "Off")}",
                () => SetSmoothMovement(!_smoothMovementRequested), KeepOpen: true),
            new(() => $"Sound: {(_sounds?.IsMuted == false ? "On" : "Off")}", () => _sounds?.ToggleMute(), KeepOpen: true),
            new(() => $"Mission Bot: {(IsBotRunning ? "On" : "Off")}",
                () => { if (IsBotRunning) DisableBot(); else EnableBot(); }, KeepOpen: true),
            new(() => $"Menu Bar: {(_menuBar?.Visible == false ? "Hidden" : "Shown")}",
                () => SetMenuBarVisible(_menuBar?.Visible == false), KeepOpen: true),
            new(() => "Controller Controls", ShowControllerControls),
            new(() => "High Scores", ShowHighScores),
            new(() => "Quit", () => _isRunning = false),
        });
        _controllerOverlay.OnOpenChanged += open => _state.IsPaused = open;

        // Show title screen
        _showingTitleScreen = true;

        return true;
    }

    /// <summary>Shows or hides the menu bar on every platform, and remembers the choice.</summary>
    private void SetMenuBarVisible(bool visible)
    {
        if (_menuBar == null || _menuBar.Visible == visible) return;
        _menuBar.Close();
        _menuBar.Visible = visible;
        _renderer?.SetMenuBarStrip(visible);

        var settings = KeyBindings.Load();
        settings.HideMenuBar = !visible;
        settings.Save();

        if (!visible)
            _messages.ShowMessage("Menu bar hidden - F10 or the pause menu brings it back", MessageType.System);
    }

    private void ShowAboutDialog()
    {
        _aboutWindow?.Open();
    }

    private void ShowHighScores()
    {
        _highScoreWindow?.Open();
    }

    private void SetGraphicsScale(int scale)
    {
        _graphicsScale = scale;
        _renderer?.SetWindowScale(scale);
        _messages.ShowMessage($"Graphics scale set to {scale}x", MessageType.System);
    }

    private void ShowKeyboardControls()
    {
        _controlsWindow?.Open(showController: false);
    }

    private void ShowControllerControls()
    {
        _controlsWindow?.Open(showController: true);
    }

    private void SelectDataFile()
    {
        // Get current directory or parent for initial location
        var initialDir = Path.GetDirectoryName(_dataPath) ?? Environment.CurrentDirectory;

        var selectedFile = UI.FileDialogHelper.ShowOpenDataFileDialog(initialDir);

        if (!string.IsNullOrEmpty(selectedFile) && File.Exists(selectedFile))
        {
            // Reload the game with the new data file
            ReloadGameData(selectedFile);
        }
        else
        {
            _messages.ShowMessage("No file selected", MessageType.System);
        }
    }

    /// <summary>Each game has its own SNDS table and files, so a new data file means new sounds.</summary>
    private void LoadSounds(string dataPath)
    {
        bool muted = _sounds?.IsMuted ?? false;
        _sounds?.Dispose();

        _sounds = new SoundManager(dataPath) { IsMuted = muted };
        _sounds.Initialize();

        int loaded = _gameData!.Sounds.Count(sound => _sounds.LoadSound(sound.Id, sound.FileName));
        Console.WriteLine($"Loaded {loaded} of {_gameData.Sounds.Count} sounds");
    }

    private void ReloadGameData(string dataFilePath)
    {
        _messages.ShowMessage($"Loading: {Path.GetFileName(dataFilePath)}...", MessageType.System);

        try
        {
            // Parse the new data file
            var parser = new DtaParser();
            var newGameData = parser.Parse(dataFilePath);

            if (newGameData == null)
            {
                _messages.ShowMessage("Failed to parse data file", MessageType.System);
                return;
            }

            // Detect game type
            string gameName = dataFilePath.ToLowerInvariant().EndsWith(".daw")
                ? "Indiana Jones"
                : "Yoda Stories";
            Console.WriteLine($"Reloading game: {gameName} from {dataFilePath}");

            // Update game data
            CancelSmoothMovement();
            ResetAreaTracking();
            _gameData = newGameData;

            // Each game has its own palette; switch before the atlas is rebuilt from it
            Palette.SetGameType(_gameData.GameType);
            _renderer?.RecreateTileAtlas(_gameData);
            LoadSounds(Path.GetDirectoryName(dataFilePath) ?? _dataPath);

            // Reset state
            _state.Reset();
            ResetPlayerAnimation();
            _messages.Clear();

            // Create new world generator
            _worldGenerator = new WorldGenerator(_gameData);

            // Show success message
            _messages.ShowMessage($"Loaded: {gameName}", MessageType.System);
            _messages.ShowMessage("Starting new game...", MessageType.Info);

            // Start a new game with the new data
            StartNewGame();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to reload game data: {ex.Message}");
            _messages.ShowMessage($"Error: {ex.Message}", MessageType.System);
        }
    }

    private void SaveGame()
    {
        // Quick save to default location
        if (_worldGenerator == null)
        {
            _messages.ShowMessage("Cannot save: No active game", MessageType.System);
            return;
        }

        var savePath = SaveGameManager.GetDefaultSavePath();
        if (SaveGameManager.SaveGame(savePath, _state, _worldGenerator))
        {
            _messages.ShowMessage("Game saved!", MessageType.System);
        }
        else
        {
            _messages.ShowMessage("Failed to save game", MessageType.System);
        }
    }

    private void SaveGameAs()
    {
        // Save As with file picker
        if (_worldGenerator == null)
        {
            _messages.ShowMessage("Cannot save: No active game", MessageType.System);
            return;
        }

        var savePath = UI.FileDialogHelper.ShowSaveSaveGameDialog();

        if (string.IsNullOrEmpty(savePath))
        {
            _messages.ShowMessage("Save cancelled", MessageType.System);
            return;
        }

        if (SaveGameManager.SaveGame(savePath, _state, _worldGenerator))
        {
            _messages.ShowMessage($"Saved to: {Path.GetFileName(savePath)}", MessageType.System);
        }
        else
        {
            _messages.ShowMessage("Failed to save game", MessageType.System);
        }
    }

    private void LoadGame()
    {
        var defaultPath = SaveGameManager.GetDefaultSavePath();
        string? savePath;

        // If default save exists, use it; otherwise show file picker
        if (File.Exists(defaultPath))
        {
            savePath = defaultPath;
        }
        else
        {
            // Show file picker
            savePath = UI.FileDialogHelper.ShowOpenSaveGameDialog();

            if (string.IsNullOrEmpty(savePath))
            {
                _messages.ShowMessage("Load cancelled", MessageType.System);
                return;
            }
        }

        var saveData = SaveGameManager.LoadGame(savePath);

        if (saveData == null)
        {
            _messages.ShowMessage("Failed to load save file", MessageType.System);
            return;
        }

        // Finish the outgoing visit before applying another game's collection flags.
        CancelSmoothMovement();
        ResetAreaTracking();
        // Apply save data to game state
        SaveGameManager.ApplyToGameState(saveData, _state);
        // Older saves kept collected weapons only in the cycling list.
        foreach (int weapon in _state.Weapons)
            if (weapon > 0 && weapon != _state.SelectedWeapon && !_state.Inventory.Contains(weapon))
                _state.AddItem(weapon);

        // Restore world map if we have world data
        if (saveData.WorldData != null && _worldGenerator != null)
        {
            RestoreWorldFromSave(saveData);
        }

        // Load the saved zone
        LoadZone(_state.CurrentZoneId, _state.PlayerX, _state.PlayerY);

        _messages.ShowMessage("Game loaded!", MessageType.System);
    }

    private void RestoreWorldFromSave(SaveGameData saveData)
    {
        if (saveData.WorldData == null || _worldGenerator == null)
            return;

        var worldData = saveData.WorldData;
        var world = new WorldMap
        {
            Planet = worldData.Planet,
            MissionNumber = worldData.MissionNumber,
            StartingZoneId = worldData.StartingZoneId,
            LandingZoneId = worldData.LandingZoneId,
            ObjectiveZoneId = worldData.ObjectiveZoneId,
            YodaZoneId = worldData.YodaZoneId,
            XWingZoneId = worldData.XWingZoneId,
            TheForceZoneId = worldData.TheForceZoneId,
            LandingPosition = (worldData.LandingPositionX, worldData.LandingPositionY),
            ObjectivePosition = (worldData.ObjectivePositionX, worldData.ObjectivePositionY),
            YodaPosition = (worldData.YodaPositionX, worldData.YodaPositionY),
            TheForcePosition = (worldData.TheForcePositionX, worldData.TheForcePositionY),
            DagobahZones = worldData.DagobahZones.ToList(),
            StartingItemId = worldData.StartingItemId,
            RequiredItems = worldData.RequiredItems.ToList(),
        };

        // Restore grid
        if (worldData.GridData.Count > 0 && worldData.GridWidth > 0 && worldData.GridHeight > 0)
        {
            world.Grid = new int?[worldData.GridHeight, worldData.GridWidth];
            int i = 0;
            for (int y = 0; y < worldData.GridHeight; y++)
            {
                for (int x = 0; x < worldData.GridWidth; x++)
                {
                    if (i < worldData.GridData.Count)
                        world.Grid[y, x] = worldData.GridData[i++];
                }
            }
        }

        // Restore connections
        world.Connections = worldData.Connections.ToDictionary(
            kv => kv.Key,
            kv => new ZoneConnections
            {
                North = kv.Value.North,
                South = kv.Value.South,
                East = kv.Value.East,
                West = kv.Value.West
            }
        );

        world.RoomConnections = new Dictionary<int, List<int>>(worldData.RoomConnections);
        world.RoomParents = new Dictionary<int, int>(worldData.RoomParents);

        // Restore mission
        if (saveData.MissionData != null)
        {
            // Older saves may have used an ending token's tile name as the mission title.
            var savedTitle = saveData.MissionData.Name;
            var titlePuzzle = _gameData!.Puzzles.FirstOrDefault(p => p.Type == PuzzleType.Mission &&
                (GetTileName(p.Item1) == savedTitle || p.Item2.HasValue && GetTileName(p.Item2.Value) == savedTitle));
            world.Mission = new Mission
            {
                MissionNumber = saveData.MissionData.MissionNumber,
                Name = titlePuzzle != null ? WorldGenerator.GetMissionTitle(titlePuzzle) : savedTitle,
                Description = saveData.MissionData.Description,
                CurrentStep = saveData.MissionData.CurrentStep,
                IsCompleted = saveData.MissionData.IsCompleted,
            };
        }

        // Set on world generator
        _worldGenerator.SetCurrentWorld(world);
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
        CancelSmoothMovement();
        ResetAreaTracking();
        _state.Reset();
        ResetPlayerAnimation();
        _messages.Clear();

        // Suppress dialogue during initialization
        if (_actionExecutor != null)
            _actionExecutor.SuppressDialogue = true;

        // Give player starting weapon based on missions completed
        // Tile 18 = Basic lightsaber (start)
        // Tile 510 = Upgraded lightsaber (after 5 missions)
        // Tile 511 = The Force (after 10 missions)
        int startingWeapon = GetWeaponForMissionCount(_state.GamesWon);
        _state.Weapons.Add(startingWeapon);
        _state.CurrentWeaponIndex = 0;
        _state.SelectedWeapon = startingWeapon;
        InitializeWeaponAmmo(startingWeapon);

        // R2D2/Locator will be spawned in Dagobah zones and must be collected by the player
        // Player starts without the locator
        _state.HasLocator = false;

        // Generate the world with selected size
        _worldGenerator = new WorldGenerator(_gameData!);
        _worldGenerator.DumpDagobahInfo();  // Debug: print Dagobah zone analysis
        var world = _worldGenerator.GenerateWorld(_selectedWorldSize);
        _state.WorldSeed = world.Seed;
        Console.WriteLine($"Generated {_selectedWorldSize} world ({world.GridWidth}x{world.GridHeight} grid), seed {world.Seed}");

        _sectorPuzzles = null;
        if (world.Sectors.Count > 0)
        {
            _sectorPuzzles = new SectorPuzzles(_gameData!, _state, world);
            _sectorPuzzles.OnSpeak += (speaker, text) => _messages.ShowDialogue(speaker, text);
            _sectorPuzzles.OnSound += id => _sounds?.PlaySound(id);
        }

        // Wire up world data to action executor for puzzle conditions
        if (_actionExecutor != null)
        {
            _actionExecutor.CurrentWorld = world;
            _actionExecutor.CurrentMission = _worldGenerator.CurrentMission;
        }

        // Welcome message - minimal startup hints (mission given by Yoda)
        // Indiana Jones has no Yoda: the mission is handed over at the start.
        _messages.ShowMessage(_gameData!.GameType == GameType.IndianaJones
            ? world.Mission?.Description ?? "" : "Find Yoda to receive your mission.", MessageType.System);

        // Load the starting zone (from Dagobah or landing zone)
        var startZoneId = world.StartingZoneId;
        if (startZoneId >= 0 && startZoneId < _gameData!.Zones.Count)
        {
            LoadZone(startZoneId, enteredByPlane: _gameData.GameType == GameType.YodaStories);
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
        }

        // Initialize debug tools
        _debugTools = new DebugTools(_gameData!, _state, _worldGenerator);
        _debugOverlay = new DebugOverlay(_gameData!, _state, _worldGenerator);
        _debugMapWindow = new DebugMapWindow(_state, _worldGenerator, _gameData!);
        _scriptViewer = new ScriptEditorWindow(_state, _gameData!);
        _scriptViewer.OnTeleportToZone += TeleportToZoneFromEditor;
        _scriptViewer.OnJumpToBot += JumpToBotZone;
        _assetViewer = new AssetViewerWindow(_gameData!);
        _saveInspector = new SaveGameEditor();
        _zoneEditor = new ZoneEditorWindow(_state, _gameData!);
        _zoneEditor.OnTeleportToZone += TeleportToZoneFromEditor;

        // Initialize bot and auto-start for debugging
        if (_worldGenerator != null)
        {
            _bot = new MissionBot(_state, _gameData!, _worldGenerator);
            _bot.OnActionRequested += HandleBotAction;
            // Don't auto-start - let user enable with B key
            // _bot.Start();
            // _messages.ShowMessage("Bot AUTO-STARTED - Press B to disable", MessageType.System);
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
    /// Teleports to a zone from the script editor.
    /// </summary>
    private void TeleportToZoneFromEditor(int zoneId)
    {
        if (zoneId < 0 || zoneId >= _gameData!.Zones.Count)
            return;

        var zone = _gameData.Zones[zoneId];
        if (zone.Width == 0 || zone.Height == 0)
        {
            _messages.ShowMessage($"Zone {zoneId} is empty", MessageType.System);
            return;
        }

        // Stop the bot during manual teleport
        _bot?.Stop();

        // Load the zone
        LoadZone(zoneId);
        _messages.ShowMessage($"Teleported to Zone {zoneId} ({zone.Planet})", MessageType.System);

        // Script viewer will now show highlights for this zone
        Console.WriteLine($"[ScriptEditor] Teleported to Zone {zoneId} - highlights should now be visible");
    }

    /// <summary>
    /// Jumps the script viewer to the bot's current zone.
    /// </summary>
    private void JumpToBotZone()
    {
        if (_bot == null || !_bot.IsRunning)
        {
            _messages.ShowMessage("Bot is not running", MessageType.System);
            return;
        }

        // The bot is in the same zone as the player
        _scriptViewer?.JumpToZone(_state.CurrentZoneId);
        _messages.ShowMessage($"Jumped to bot's zone: {_state.CurrentZoneId}", MessageType.System);
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

            case BotActionType.Pull:
            {
                var (pdx, pdy) = dir switch { Direction.Up => (0, -1), Direction.Down => (0, 1), Direction.Left => (-1, 0), _ => (1, 0) };
                TryMovePlayer(pdx, pdy, dir, tryPull: true);
                break;
            }

            case BotActionType.Attack:
                _state.PlayerDirection = dir;
                PerformAttack();
                break;

            case BotActionType.Talk:
                // For talk action, temporarily clear selected item so UseItem() triggers dialogue
                _state.PlayerDirection = dir;
                var savedItem = _state.SelectedItem;
                _state.SelectedItem = null;
                UseItem();
                _state.SelectedItem = savedItem; // Restore item after talking
                break;

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
    public void LoadZone(int zoneId, int? spawnX = null, int? spawnY = null, bool enteredByPlane = false)
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

        CancelSmoothMovement();
        if (!ReferenceEquals(_state.CurrentZone, zone))
        {
            ResetUnsolvedArea();
            _state.Projectiles.Clear();
        }
        CaptureInitialArea(zone);
        _state.CurrentZoneId = zoneId;
        _state.CurrentZone = zone;
        _actionExecutor?.ResetForZone();
        if (enteredByPlane && _gameData.GameType == GameType.YodaStories)
            PrepareXWingLanding(zone);
        _state.MarkZoneVisited(zoneId);
        // HideHero is zone-script state. A new zone must not inherit a hidden hero from a
        // previous bump or interrupted cutscene; zone-entry actions can hide it again.
        _state.HeroVisible = true;

        // Notify script viewer of zone change
        _scriptViewer?.JumpToCurrentZone();

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

        // Validate spawn position - find walkable tile if spawn is blocked
        if (!enteredByPlane && !IsSpawnPositionWalkable(_state.PlayerX, _state.PlayerY, zone))
        {
            Console.WriteLine($"Spawn position ({_state.PlayerX},{_state.PlayerY}) is blocked, searching for walkable tile...");
            var walkable = FindNearestWalkablePosition(_state.PlayerX, _state.PlayerY, zone);
            if (walkable.HasValue)
            {
                _state.PlayerX = walkable.Value.x;
                _state.PlayerY = walkable.Value.y;
                Console.WriteLine($"Found walkable spawn at ({_state.PlayerX},{_state.PlayerY})");
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


        // Debug: Show action count for zones with scripts
        if (zone.Actions.Any(a => a.Instructions.Count > 0))
        {
            int totalInstructions = zone.Actions.Sum(a => a.Instructions.Count);
            Console.WriteLine($"  Zone has {zone.Actions.Count} scripts ({totalInstructions} instructions)");
        }

        _sectorPuzzles?.LayDown(zone);

        // Initialize NPCs from zone objects
        InitializeZoneNPCs();

        // Spawn Yoda if this is his zone
        SpawnYodaIfNeeded(zoneId);

        // R2D2 exists as tile 794 in the zone - no need to spawn, just detect when player steps on it

        // Check for X-Wing in this zone
        CheckForXWing(zoneId);

        // Reset camera for zone
        UpdateCamera();

        // Execute zone entry actions (suppress dialogue - it should only show from player interaction)
        if (_actionExecutor != null)
        {
            _actionExecutor.SuppressDialogue = true;
            _actionExecutor.ExecuteZoneActions(ActionTrigger.ZoneEnter, enteredByPlane);
            _actionExecutor.SuppressDialogue = false;
        }

        // Entry scripts choose which alternative reward hotspots are enabled.
        _sectorPuzzles?.Refresh(zone);

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
            _sounds?.Update();

            // Docs capture harness - drives the shot list, then stops the loop
            if (Dev.Capture.Enabled)
                CaptureTick(deltaTime);
            else if (BotRunSeconds > 0)
                BotRunTick(deltaTime);

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
            // Handle Quit event FIRST - before any windows can consume it
            if ((SDLEventType)evt.Type == SDLEventType.Quit)
            {
                _isRunning = false;
                return; // Exit immediately
            }

            // Handle main window close event (when other windows are open, SDL may not send Quit)
            if ((SDLEventType)evt.Type == SDLEventType.Windowevent)
            {
                // Check if this is the main window being closed
                uint mainWindowId = _renderer.GetWindowID();
                if (evt.Window.WindowID == mainWindowId)
                {
                    if (evt.Window.Event == (byte)SDLWindowEventID.FocusLost)
                    { _movementFocused = false; CancelSmoothMovement(); }
                    else if (evt.Window.Event == (byte)SDLWindowEventID.FocusGained)
                    { _movementFocused = true; _suppressHeldMovement = true; _discardMovementTime = true; }
                }
                if (evt.Window.WindowID == mainWindowId &&
                    evt.Window.Event == (byte)SDLWindowEventID.Close)
                {
                    _isRunning = false;
                    return; // Exit immediately
                }
            }

            // The locator owns input while its map is open, before game menus or
            // inventory shortcuts can interpret keys from the secondary window.
            if (_debugMapWindow != null)
            {
                SDLEvent mapEvent = evt;
                if (_debugMapWindow.HandleEvent(&mapEvent)) continue;
                if (_debugMapWindow.IsLocatorOpen) continue;
            }

            if ((SDLEventType)evt.Type == SDLEventType.Keydown && evt.Key.Keysym.Sym == (int)SDLKeyCode.F10 &&
                _menuBar is { Visible: false })
            {
                SetMenuBarVisible(true);
                continue;
            }

            // The menu bar goes first, and on the title screen too - New Game lives in it.
            // It owns the reserved strip and so wants raw window coordinates; everything
            // after it thinks in game-area coordinates, hence the shift.
            if (_menuBar != null)
            {
                SDLEvent evtCopy = evt;
                if (_menuBar.HandleEvent(&evtCopy))
                    continue;
            }

            if (_controllerOverlay != null && !_showingTitleScreen)
            {
                SDLEvent evtCopy = evt;
                if (_controllerOverlay.HandleEvent(&evtCopy))
                    continue;
            }

            ShiftPointerIntoGameArea(&evt);

            // Handle title screen events
            if (_showingTitleScreen && _titleScreen != null)
            {
                SDLEvent evtCopy = evt;
                if (_titleScreen.HandleEvent(&evtCopy))
                    continue;
            }

            // Handle debug window events
            if (_debugMapWindow != null)
            {
                SDLEvent evtCopy = evt;
                if (_debugMapWindow.HandleEvent(&evtCopy))
                    continue;
            }
            if (_scriptViewer != null)
            {
                SDLEvent evtCopy = evt;
                if (_scriptViewer.HandleEvent(&evtCopy))
                    continue;
            }
            if (_assetViewer != null)
            {
                SDLEvent evtCopy = evt;
                if (_assetViewer.HandleEvent(&evtCopy))
                    continue;
            }
            if (_saveInspector != null)
            {
                SDLEvent evtCopy = evt;
                if (_saveInspector.HandleEvent(&evtCopy))
                    continue;
            }
            if (_zoneEditor != null)
            {
                SDLEvent evtCopy = evt;
                if (_zoneEditor.HandleEvent(&evtCopy))
                    continue;
            }
            if (_controlsWindow != null)
            {
                SDLEvent evtCopy = evt;
                if (_controlsWindow.HandleEvent(&evtCopy))
                    continue;
            }
            if (_aboutWindow != null)
            {
                SDLEvent evtCopy = evt;
                if (_aboutWindow.HandleEvent(&evtCopy))
                    continue;
            }
            if (_scoreWindow != null && _scoreWindow.IsOpen)
            {
                SDLEvent evtCopy = evt;
                if (_scoreWindow.HandleEvent(&evtCopy))
                    continue;
            }
            if (_highScoreWindow != null && _highScoreWindow.IsOpen)
            {
                SDLEvent evtCopy = evt;
                if (_highScoreWindow.HandleEvent(&evtCopy))
                    continue;
            }

            switch ((SDLEventType)evt.Type)
            {

                case SDLEventType.Keydown:
                    if (_smoothMovement && (IsSmoothMovementKey(evt.Key.Keysym.Sym) ||
                        evt.Key.Keysym.Sym is (int)SDLKeyCode.Up or (int)SDLKeyCode.Down or (int)SDLKeyCode.Left or (int)SDLKeyCode.Right or
                            (int)SDLKeyCode.W or (int)SDLKeyCode.A or (int)SDLKeyCode.S or (int)SDLKeyCode.D)) break;
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
                    // Which is an instance id; only let go if it is the pad we are using.
                    if (_controllerConnected &&
                        SDL.JoystickInstanceID(SDL.GameControllerGetJoystick(_controller)) == evt.Cdevice.Which)
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

                case SDLEventType.Mousewheel:
                    // Scroll inventory when mouse wheel is used
                    // Get current mouse position to check if over sidebar
                    int mouseX, mouseY;
                    SDL.GetMouseState(&mouseX, &mouseY);
                    if (_renderer != null && _renderer.IsPointOverSidebar(mouseX, mouseY))
                    {
                        if (evt.Wheel.Y > 0)
                            _renderer.ScrollInventoryUp();
                        else if (evt.Wheel.Y < 0)
                            _renderer.ScrollInventoryDown(_state.Inventory.Count);
                    }
                    break;

                case SDLEventType.Mousebuttondown:
                    HandleMouseDown(evt.Button.X, evt.Button.Y, evt.Button.Button);
                    break;

                case SDLEventType.Mousebuttonup:
                    HandleMouseUp(evt.Button.X, evt.Button.Y, evt.Button.Button);
                    break;

                case SDLEventType.Mousemotion:
                    HandleMouseMove(evt.Motion.X, evt.Motion.Y);
                    break;
            }
        }

        // Handle analog stick movement (called every frame)
        if (!_smoothMovement && _controllerConnected && _controller != null && _controllerOverlay?.IsOpen != true)
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
    /// - Start/Back: the pause overlay, which UI.ControllerOverlay handles before this
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
        const byte SDL_CONTROLLER_BUTTON_LEFTSHOULDER = 9;
        const byte SDL_CONTROLLER_BUTTON_RIGHTSHOULDER = 10;
        const byte SDL_CONTROLLER_BUTTON_DPAD_UP = 11;
        const byte SDL_CONTROLLER_BUTTON_DPAD_DOWN = 12;
        const byte SDL_CONTROLLER_BUTTON_DPAD_LEFT = 13;
        const byte SDL_CONTROLLER_BUTTON_DPAD_RIGHT = 14;

        if (_smoothMovement && button >= SDL_CONTROLLER_BUTTON_DPAD_UP && button <= SDL_CONTROLLER_BUTTON_DPAD_RIGHT) return;
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

    private void HandleMouseDown(int x, int y, byte button)
    {
        // Only handle left click (button 1)
        if (button != 1) return;

        if (_renderer == null) return;
        if (MovementUIBlocked || _state.IsGameOver || !_state.HeroVisible) return;

        // Check if click is on weapon slot
        if (_renderer.IsPointOverWeaponSlot(x, y) && _state.SelectedWeapon.HasValue)
        {
            // Start dragging the equipped weapon
            _isDragging = true;
            _draggedItemId = _state.SelectedWeapon.Value;
            _draggedFromSlot = -1;  // -1 indicates weapon slot
            _draggedFromZone = _state.CurrentZoneId;
            _dragStartX = x;
            _dragStartY = y;
            _dragCurrentX = x;
            _dragCurrentY = y;
            return;
        }

        // Check if click is on inventory (in sidebar)
        if (_renderer.IsPointOverSidebar(x, y))
        {
            // Check if clicking on an inventory slot
            int? clickedSlot = _renderer.GetInventorySlotAtPosition(x, y, _state.Inventory.Count);
            Console.WriteLine($"MouseDown on sidebar: x={x}, y={y}, inventoryCount={_state.Inventory.Count}, clickedSlot={clickedSlot}");
            if (clickedSlot.HasValue && clickedSlot.Value < _state.Inventory.Count)
            {
                var itemId = _state.Inventory[clickedSlot.Value];

                // Start dragging this item
                _isDragging = true;
                _draggedItemId = itemId;
                _draggedFromSlot = clickedSlot.Value;
                _draggedFromZone = _state.CurrentZoneId;
                _dragStartX = x;
                _dragStartY = y;
                _dragCurrentX = x;
                _dragCurrentY = y;

                // Also select the item
                _state.SelectedItem = itemId;
            }
        }
    }

    private void HandleMouseUp(int x, int y, byte button)
    {
        // Only handle left click (button 1)
        if (button != 1) return;

        if (_draggedFromZone != _state.CurrentZoneId || MovementUIBlocked || _state.IsGameOver || !_state.HeroVisible)
        {
            ClearItemDrag();
            return;
        }

        if (_isDragging && _draggedItemId.HasValue && _renderer != null)
        {
            int dragDist = Math.Abs(x - _dragStartX) + Math.Abs(y - _dragStartY);
            bool wasActualDrag = dragDist >= 10;

            // Check if dropped on weapon slot
            if (_renderer.IsPointOverWeaponSlot(x, y))
            {
                if (wasActualDrag && IsWeaponTile(_draggedItemId.Value))
                {
                    // EquipWeapon transfers one copy between the bag and weapon slot.
                    // Equip the new weapon
                    EquipWeapon(_draggedItemId.Value);
                    _messages.ShowMessage("Equipped weapon", MessageType.Info);
                }
            }
            // Check if dropped on inventory
            else if (_renderer.IsPointOverSidebar(x, y))
            {
                int? targetSlot = _renderer.GetInventorySlotAtPosition(x, y, _state.Inventory.Count);
                Console.WriteLine($"Drop on sidebar: targetSlot={targetSlot}, fromSlot={_draggedFromSlot}, wasActualDrag={wasActualDrag}");

                if (wasActualDrag)
                {
                    // Dragging from weapon slot to inventory - unequip
                    if (_draggedFromSlot.HasValue && _draggedFromSlot.Value == -1)
                    {
                        // Add the weapon to inventory
                        _state.Inventory.Add(_draggedItemId.Value);
                        _state.SelectedWeapon = null;
                        _messages.ShowMessage("Unequipped weapon", MessageType.Info);
                    }
                    // Dragging between inventory slots - swap
                    else if (_draggedFromSlot.HasValue && _draggedFromSlot.Value >= 0 && targetSlot.HasValue)
                    {
                        int fromSlot = _draggedFromSlot.Value;
                        int toSlot = targetSlot.Value;

                        Console.WriteLine($"Drag: fromSlot={fromSlot}, toSlot={toSlot}, inventoryCount={_state.Inventory.Count}");

                        // Clamp target slot to valid range
                        if (toSlot >= _state.Inventory.Count)
                            toSlot = _state.Inventory.Count - 1;

                        if (toSlot >= 0 && toSlot < _state.Inventory.Count && fromSlot != toSlot)
                        {
                            // Swap the items
                            var temp = _state.Inventory[toSlot];
                            _state.Inventory[toSlot] = _state.Inventory[fromSlot];
                            _state.Inventory[fromSlot] = temp;
                            Console.WriteLine($"Swapped items: slot {fromSlot} <-> slot {toSlot}");
                        }
                    }
                }
            }
            else if (wasActualDrag && x >= 0 && y >= 0 &&
                x < GameRenderer.GameAreaWidth && y < GameRenderer.GameAreaHeight)
            {
                // Mouse coordinates have already had the menu strip removed.
                // Use the same fractional camera origin as the world renderer.
                int targetX = (int)Math.Floor(ViewCameraX + x / (double)(Tile.Width * GameRenderer.Scale));
                int targetY = (int)Math.Floor(ViewCameraY + y / (double)(Tile.Height * GameRenderer.Scale));
                UseItemAt(_draggedItemId.Value, targetX, targetY, _draggedFromZone!.Value);
            }
        }

        ClearItemDrag();
    }

    private void ClearItemDrag()
    {
        _isDragging = false;
        _draggedItemId = null;
        _draggedFromSlot = null;
        _draggedFromZone = null;
    }

    private void HandleMouseMove(int x, int y)
    {
        if (_isDragging)
        {
            _dragCurrentX = x;
            _dragCurrentY = y;
        }
    }

    /// <summary>
    /// Checks if a tile is a weapon that can be equipped.
    /// </summary>
    private bool IsWeaponTile(int tileId)
    {
        if (tileId < 0 || tileId >= _gameData!.Tiles.Count)
            return false;

        var tile = _gameData.Tiles[tileId];

        // Check for weapon flag
        if ((tile.Flags & TileFlags.Weapon) != 0)
            return true;

        // Check known weapon tiles
        if (tileId == TILE_BASIC_LIGHTSABER || tileId == TILE_UPGRADED_LIGHTSABER || tileId == TILE_THE_FORCE)
            return true;

        // Check by tile name
        var name = GetTileName(tileId);
        if (name != null)
        {
            var nameLower = name.ToLowerInvariant();
            if (nameLower.Contains("blaster") || nameLower.Contains("saber") || nameLower.Contains("force") ||
                nameLower.Contains("rifle") || nameLower.Contains("pistol") || nameLower.Contains("weapon"))
                return true;
        }

        return false;
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
        const int SDLK_0 = 48;
        const int SDLK_1 = 49;
        const int SDLK_8 = 56;
        const int SDLK_9 = 57;
        const int SDLK_PAGEUP = 1073741899;
        const int SDLK_PAGEDOWN = 1073741902;
        const int SDLK_LBRACKET = 91;  // [ for scroll up
        const int SDLK_RBRACKET = 93;  // ] for scroll down
        const int SDLK_TAB = 9;
        const int SDLK_a = 97;
        const int SDLK_b = 98;
        const int SDLK_d = 100;
        const int SDLK_f = 102;
        const int SDLK_i = 105;
        const int SDLK_m = 109;
        const int SDLK_n = 110;
        const int SDLK_o = 111;
        const int SDLK_p = 112;
        const int SDLK_r = 114;
        const int SDLK_s = 115;
        const int SDLK_w = 119;
        const int SDLK_x = 120;
        const int SDLK_F1 = 1073741882;
        const int SDLK_F2 = 1073741883;

        // Handle debug overlay input first
        if (_debugOverlay?.IsVisible == true)
        {
            switch (keyCode)
            {
                case SDLK_F1:
                    _debugOverlay.Toggle();
                    return;
                case SDLK_LEFT:
                    _debugOverlay.PrevTab();
                    return;
                case SDLK_RIGHT:
                    _debugOverlay.NextTab();
                    return;
                case SDLK_UP:
                    _debugOverlay.ScrollUp();
                    return;
                case SDLK_DOWN:
                    _debugOverlay.ScrollDown();
                    return;
                case SDLK_ESCAPE:
                    _debugOverlay.Toggle();
                    return;
            }
        }

        switch (keyCode)
        {
            case SDLK_F1:  // F1 - Toggle debug overlay
                _debugOverlay?.Toggle();
                break;

            case 1073741883:  // F2 - Toggle debug map window
                _debugMapWindow?.Toggle();
                break;

            case 1073741884:  // F3 - Toggle script viewer
                _scriptViewer?.Toggle();
                break;

            case 1073741885:  // F4 - Toggle asset viewer
                _assetViewer?.Toggle();
                break;

            case 1073741886:  // F5 - Scale 1x
                SetGraphicsScale(1);
                break;

            case 1073741887:  // F6 - Scale 2x (default)
                SetGraphicsScale(2);
                break;

            case 1073741888:  // F7 - Scale 4x
                SetGraphicsScale(4);
                break;

            case 1073741889:  // F8 - Save Game Inspector
                _saveInspector?.Toggle();
                break;

            case 1073741890:  // F9 - Zone Editor
                _zoneEditor?.Toggle();
                break;

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

            case >= SDLK_1 and <= SDLK_9:
            case SDLK_0:
                // Select inventory item (keys 1-9, 0 for slot 10)
                // Slot number is affected by scroll offset
                int slotNum = keyCode == SDLK_0 ? 10 : keyCode - SDLK_1 + 1;
                int inventoryIdx = _renderer!.GetInventoryIndexForSlot(slotNum, _state.Inventory.Count);
                if (inventoryIdx >= 0 && inventoryIdx < _state.Inventory.Count)
                {
                    _state.SelectedItem = _state.Inventory[inventoryIdx];
                    var itemName = GetTileName(_state.Inventory[inventoryIdx]) ?? $"Item {inventoryIdx + 1}";
                    _messages.ShowMessage($"Selected: {itemName}", MessageType.Info);
                }
                else
                {
                    _messages.ShowMessage($"Slot {slotNum} is empty", MessageType.Info);
                }
                break;

            case SDLK_PAGEUP:
            case SDLK_LBRACKET:
                // Scroll inventory up
                _renderer?.ScrollInventoryUp();
                break;

            case SDLK_PAGEDOWN:
            case SDLK_RBRACKET:
                // Scroll inventory down
                _renderer?.ScrollInventoryDown(_state.Inventory.Count);
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

            case SDLK_i:  // I key - inspect/debug dump
                _debugTools?.DumpAll();
                _messages.ShowMessage("Debug info dumped to console", MessageType.System);
                break;

            case SDLK_TAB:  // Tab - toggle weapon
                ToggleWeapon();
                break;
        }
    }

    private void RegisterScriptedItem(int item)
    {
        if (IsWeaponTile(item)) RegisterWeapon(item);
        if (_gameData!.GameType == GameType.YodaStories && item == WorldGenerator.TILE_LOCATOR)
            _state.HasLocator = true;
        _sectorPuzzles?.OnPickedUp(item);
        SyncSectorMission();
    }

    private void RegisterWeapon(int weaponId)
    {
        if (!_state.Weapons.Contains(weaponId))
            _state.Weapons.Add(weaponId);
        InitializeWeaponAmmo(weaponId);
    }

    private void CollectWeapon(int weaponId)
    {
        RegisterWeapon(weaponId);
        // Every pickup is a real copy, including a spare of the equipped weapon.
        if (weaponId > 0) _state.AddItem(weaponId);
        if (_state.SelectedWeapon == weaponId && _state.IsWeaponEmpty(weaponId))
            _state.RemoveDepletedWeapon(weaponId);
    }

    private void EquipWeapon(int weaponId)
    {
        RegisterWeapon(weaponId);
        if (_state.SelectedWeapon != (weaponId == 0 ? (int?)null : weaponId))
        {
            if (_state.SelectedWeapon is { } previous && previous > 0)
                _state.AddItem(previous);
            _state.Inventory.Remove(weaponId);
        }
        _state.CurrentWeaponIndex = _state.Weapons.IndexOf(weaponId);
        _state.SelectedWeapon = weaponId == 0 ? null : weaponId;
        _state.SelectedItem = null;
        if (weaponId != 0) PlayEquipSound(weaponId);
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
        EquipWeapon(weaponId);

        if (weaponId == 0)
        {
            _state.SelectedWeapon = null;
            _messages.ShowMessage("Equipped: Fists", MessageType.Info);
        }
        else
        {
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
        // Read the physical keys as well as SDL's modifier cache. Window/menu event
        // handling can leave the cache behind the keyboard state on some platforms.
        int count;
        var keys = SDL.GetKeyboardState(&count);
        int left = (int)SDL.GetScancodeFromKey((int)SDLKeyCode.Lshift);
        int right = (int)SDL.GetScancodeFromKey((int)SDLKeyCode.Rshift);
        return left >= 0 && left < count && keys[left] != 0 ||
            right >= 0 && right < count && keys[right] != 0 ||
            (SDL.GetModState() & SDLKeymod.Shift) != 0;
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
        MarkObjectsMoved(_state.CurrentZone);
        _state.CurrentZone.SetTile(oldPlayerX, oldPlayerY, 1, behindTile);
        _state.CurrentZone.SetTile(behindX, behindY, 1, 0xFFFF);
        _messages.ShowMessage("*pull*", MessageType.Info);
        _sectorPuzzles?.Refresh(_state.CurrentZone); // whatever the block was covering shows now
    }

    private bool IsScriptedActorTile(Zone zone, int x, int y)
    {
        ushort tileId = zone.GetTile(x, y, 1);
        if (tileId >= _gameData!.Tiles.Count) return false;
        var tile = _gameData.Tiles[tileId];
        if (tile.IsObject || !(tile.IsItem || tile.IsWeapon || tile.IsCharacter)) return false;
        // A trade target remains an actor until its script explicitly hands it over.
        if (zone.Actions.Any(a => a.Conditions.Any(c =>
            c.Opcode is ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot && c.Arguments.Count >= 4 &&
            c.Arguments[0] == x && c.Arguments[1] == y && c.Arguments[2] == 1 && c.Arguments[3] == tileId))) return true;
        // Flavor text which has finished/disabled itself must not permanently lock a pickup.
        if (_actionExecutor != null && ReferenceEquals(zone, _state.CurrentZone))
            return _actionExecutor.WouldHandleBump(x, y);
        return zone.Actions.Select((action, index) => (action, index)).Any(pair =>
            !_state.DisabledActions.Contains((zone.Id, pair.index)) && pair.action.Conditions.Any(c =>
                c.Opcode == ConditionOpcode.Bump && c.Arguments.Count >= 3 &&
                c.Arguments[0] == x && c.Arguments[1] == y && c.Arguments[2] == tileId));
    }

    private void TryMovePlayer(int dx, int dy, Direction direction, bool tryPull = false)
    {
        if (_smoothMovement && !_committingSmoothStep)
        {
            RequestSmoothPlayerStep(dx, dy, direction, tryPull);
            return;
        }
        if (_actionExecutor?.IsBusy == true || !_state.HeroVisible)
            return;

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

        // X-Wing hotspots can sit on a solid vehicle tile. Route boarding before the
        // normal collision path calls BumpInto; otherwise the player stays in this zone
        // and a bump script can hide the hero without ever traveling.
        var xwingHotspot = _state.CurrentZone.Objects.FirstOrDefault(obj =>
            obj.Enabled && obj.X == newX && obj.Y == newY &&
            obj.Type is ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah);
        if (xwingHotspot != null)
        {
            TravelToPlanet(xwingHotspot);
            return;
        }

        // The sector's trading NPC is a tile that talks when bumped
        if (_sectorPuzzles?.Bump(newX, newY) == true)
        {
            SyncSectorMission();
            return;
        }

        if (IsScriptedActorTile(_state.CurrentZone, newX, newY))
        {
            if (dx == 0 || dy == 0) BumpInto(newX, newY);
            return;
        }

        if (IsGroundBlocked(_state.CurrentZone, newX, newY))
        {
            BumpInto(newX, newY);
            return;
        }

        // Check collision with middle layer tile
        var middleTile = _state.CurrentZone.GetTile(newX, newY, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            bool diagonal = dx != 0 && dy != 0;

            if (tile.IsDraggable && dx != 0 && dy != 0) return;
            if (tile.IsDraggable)
            {
                // Try to push the block
                var pushX = newX + dx;
                var pushY = newY + dy;

                // Check if push destination is valid
                if (pushX >= 0 && pushX < _state.CurrentZone.Width &&
                    pushY >= 0 && pushY < _state.CurrentZone.Height)
                {
                    // Only into an empty square, as the original (WebFun hero.ts _doDrag): a block
                    // pushed onto an item used to overwrite it.
                    var destTile = _state.CurrentZone.GetTile(pushX, pushY, 1);
                    if (!IsGroundBlocked(_state.CurrentZone, pushX, pushY) && destTile == 0xFFFF)
                    {
                        // Push the block
                        MarkObjectsMoved(_state.CurrentZone);
                        _state.CurrentZone.SetTile(pushX, pushY, 1, middleTile);
                        _state.CurrentZone.SetTile(newX, newY, 1, 0xFFFF);
                        _messages.ShowMessage("*push*", MessageType.Info);
                        // A crate is often sitting on a hidden item; uncovering it shows it
                        _sectorPuzzles?.Refresh(_state.CurrentZone);
                        // Continue to move player into the now-empty space
                    }
                    else
                    {
                        // Can't push - something blocking
                        BumpInto(newX, newY);
                        return;
                    }
                }
                else
                {
                    // Can't push off edge
                    BumpInto(newX, newY);
                    return;
                }
            }
            else if (tile.IsSolid && !IsNPCMapSprite(newX, newY, middleTile))
            {
                // Collision - trigger bump action
                var bumpZone = _state.CurrentZone;
                BumpInto(newX, newY);
                if (!_smoothMovement && !tryPull && (dx == 0 || dy == 0) &&
                    ReferenceEquals(bumpZone, _state.CurrentZone) && !_messages.HasDialogue &&
                    _actionExecutor?.IsBusy != true && _state.HeroVisible)
                    foreach (var slide in dx == 0 ? new[] { (-1, dy), (1, dy) } : new[] { (dx, 1), (dx, -1) })
                        if (CanSlidePlayerTo(slide.Item1, slide.Item2))
                        {
                            TryMovePlayer(slide.Item1, slide.Item2, direction);
                            break;
                        }
                return;
            }
        }

        // A tile a bump script is waiting for is bumped, not walked over - in the original
        // anything in the object layer stops you. (Pacal is an item tile; walking onto him
        // picked him up and skipped the thank-you that wins the game.)
        if (middleTile != 0xFFFF && _actionExecutor?.WouldHandleBump(newX, newY) == true)
        {
            BumpInto(newX, newY);
            return;
        }
        // Reserved destinations cannot be entered while another actor is moving.
        if (_smoothMovement && MovementReserved(newX, newY)) return;

        // Check collision with NPCs
        NPC? r2d2ToCollect = null;
        foreach (var npc in _state.ZoneNPCs)
        {
            if (npc.IsEnabled && npc.IsAlive && npc.X == newX && npc.Y == newY)
            {
                // Special case: R2D2 that's ready to collect can be walked over
                if (npc.IsReadyToCollect && IsR2D2Character(npc.CharacterId))
                {
                    r2d2ToCollect = npc;
                    continue;
                }
                // Can't walk through other NPCs
                return;
            }
        }

        // Remember old position for pull
        var oldX = _state.PlayerX;
        var oldY = _state.PlayerY;

        // Move player
        _state.PlayerX = newX;
        _state.PlayerY = newY;

        if (_playerWalkingTimer <= 0)
        {
            _playerAnimationFrame = 0;
            _playerAnimationTimer = 0;
        }
        _playerWalkingTimer = AnimationFrameTime * 3;

        // Collect R2D2 if we walked over it
        if (r2d2ToCollect != null)
        {
            CollectR2D2(r2d2ToCollect);
        }

        // Pull block if shift is held
        if (tryPull)
        {
            TryPullBlock(oldX, oldY, dx, dy);
        }

        // Update camera
        UpdateCamera();

        var arrivalZone = _state.CurrentZone;
        // Execute walk actions
        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);
        if (ReferenceEquals(arrivalZone, _state.CurrentZone))
            _sectorPuzzles?.Refresh(_state.CurrentZone!);

        // Check for zone objects at new position
        CheckZoneObjects();
        if (!ReferenceEquals(arrivalZone, _state.CurrentZone)) return;

        // Check for Item tiles on layer 1 that can be picked up
        var objTile = _state.CurrentZone.GetTile(newX, newY, 1);
        if (objTile != 0xFFFF && objTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[objTile];
            Console.WriteLine($"Standing on tile {objTile}: floor={tile.IsFloor}, obj={tile.IsObject}, item={tile.IsItem}, char={tile.IsCharacter}");

            // Auto-pickup loose items and weapons; solid objects still require interaction.
            if ((tile.IsItem || tile.IsWeapon) && !tile.IsObject &&
                !IsScriptedActorTile(_state.CurrentZone, newX, newY))
            {
                var key = $"{_state.CurrentZoneId}_{newX}_{newY}_tile";
                if (!_state.CollectedObjects.Contains(key))
                {
                    // Pick up the item
                    // A hotspot may already have awarded the same pickup on arrival.
                    var awardedByHotspot = _state.IsObjectCollected(_state.CurrentZoneId, newX, newY) &&
                        _state.CurrentZone.Objects.Any(o => o.Enabled && o.X == newX && o.Y == newY &&
                            o.Argument == objTile && o.Type is ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon);
                    if (!awardedByHotspot)
                    {
                        if (tile.IsWeapon) CollectWeapon(objTile);
                        else _state.AddItem(objTile);
                    }
                    _state.CollectedObjects.Add(key);

                    // Remove the tile from the map
                    _state.CurrentZone.SetTile(newX, newY, 1, 0xFFFF);

                    var itemName = GetTileName(objTile) ?? $"Item";
                    if (!awardedByHotspot) _messages.ShowPickup(itemName);
                    Console.WriteLine($"Auto-picked up tile item {objTile} ({itemName}) at ({newX},{newY})");
                    _sectorPuzzles?.OnPickedUp(objTile);
                    SyncSectorMission();
                }
            }

            // Special case: R2D2 (tile 794) - collect when stepped on
            if (objTile == WorldGenerator.TILE_LOCATOR && !_state.HasLocator)
            {
                var key = $"{_state.CurrentZoneId}_{newX}_{newY}_r2d2";
                if (!_state.CollectedObjects.Contains(key))
                {
                    // Collect R2D2
                    _state.AddItem(WorldGenerator.TILE_LOCATOR);
                    _state.HasLocator = true;
                    _state.CollectedObjects.Add(key);

                    // Remove from map
                    _state.CurrentZone.SetTile(newX, newY, 1, 0xFFFF);

                    _messages.ShowDialogue("R2-D2", "*happy beeps* I'm ready to help with your mission!");
                    _messages.ShowPickup("R2-D2 (Locator)");
                    _messages.ShowMessage("Use R2-D2 from your inventory for hints!", MessageType.Info);
                    Console.WriteLine($"Collected R2D2 tile at ({newX},{newY})");
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

    /// <summary>
    /// Checks if a position is walkable (not blocked by walls or objects).
    /// </summary>
    private bool IsGroundBlocked(Zone zone, int x, int y)
    {
        int ground = zone.GetTile(x, y, 0);
        if (ground >= _gameData!.Tiles.Count || !_gameData.Tiles[ground].IsObject) return false;
        // A walkable surface such as a bridge can cover otherwise impassable ground.
        int surface = zone.GetTile(x, y, 1);
        return surface >= _gameData.Tiles.Count || !_gameData.Tiles[surface].IsFloor ||
            _gameData.Tiles[surface].IsObject;
    }

    private bool IsSpawnPositionWalkable(int x, int y, Zone zone)
    {
        // Check bounds
        if (x < 0 || x >= zone.Width || y < 0 || y >= zone.Height)
            return false;

        if (IsGroundBlocked(zone, x, y)) return false;
        if (IsScriptedActorTile(zone, x, y)) return false;

        // Check middle layer tile
        var middleTile = zone.GetTile(x, y, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            if (tile.IsSolid && !tile.IsDraggable)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Finds the nearest walkable position to a target position using spiral search.
    /// </summary>
    private (int x, int y)? FindNearestWalkablePosition(int startX, int startY, Zone zone)
    {
        // Spiral search pattern
        int maxRadius = Math.Max(zone.Width, zone.Height);

        for (int radius = 1; radius < maxRadius; radius++)
        {
            // Search in a square pattern around the start point
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Only check perimeter of the square
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;

                    int x = startX + dx;
                    int y = startY + dy;

                    if (IsSpawnPositionWalkable(x, y, zone))
                        return (x, y);
                }
            }
        }

        // Fallback: try center of zone
        int centerX = zone.Width / 2;
        int centerY = zone.Height / 2;
        if (IsSpawnPositionWalkable(centerX, centerY, zone))
            return (centerX, centerY);

        // Last resort: find any walkable position
        for (int y = 1; y < zone.Height - 1; y++)
        {
            for (int x = 1; x < zone.Width - 1; x++)
            {
                if (IsSpawnPositionWalkable(x, y, zone))
                    return (x, y);
            }
        }

        return null;
    }

    private void HandleZoneTransition(int dx, int dy)
    {
        // Check for transition objects at player position and edge position
        var edgeX = _state.PlayerX + dx;
        var edgeY = _state.PlayerY + dy;

        // First check for door/teleporter objects
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            if (!obj.Enabled)
                continue;

            // Check if this is a transition object at player pos or just past edge
            bool atPlayerPos = obj.X == _state.PlayerX && obj.Y == _state.PlayerY;
            bool atEdge = obj.X == Math.Clamp(edgeX, 0, _state.CurrentZone.Width - 1) &&
                          obj.Y == Math.Clamp(edgeY, 0, _state.CurrentZone.Height - 1);

            if (!atPlayerPos && !atEdge)
                continue;

            switch (obj.Type)
            {
                case ZoneObjectType.XWingFromDagobah:
                case ZoneObjectType.XWingToDagobah:
                    TravelToPlanet(obj);
                    return;

                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                    TravelThroughDoor(obj);
                    return;

                case ZoneObjectType.VehicleToSecondary:
                case ZoneObjectType.VehicleToPrimary:
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

                        LoadZone(destZoneId, spawnX, spawnY,
                            enteredByPlane: obj.Type is ZoneObjectType.VehicleToSecondary or ZoneObjectType.VehicleToPrimary);
                        return;
                    }
                    break;
            }
        }

        // Check for edge transition using world generator connections
        // IMPORTANT: Only allow transition if both the current edge and destination entry are walkable
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

            if (connectedZoneId.HasValue && connectedZoneId.Value < _gameData!.Zones.Count)
            {
                var destZone = _gameData.Zones[connectedZoneId.Value];

                // Calculate spawn position on opposite edge
                int spawnX, spawnY;
                int edgeCheckX, edgeCheckY; // Position to check in destination zone

                if (dx < 0)
                {
                    // Going left - spawn on right edge of dest
                    spawnX = destZone.Width - 2;
                    spawnY = Math.Clamp(_state.PlayerY, 1, destZone.Height - 2);
                    edgeCheckX = destZone.Width - 1;
                    edgeCheckY = spawnY;
                }
                else if (dx > 0)
                {
                    // Going right - spawn on left edge of dest
                    spawnX = 1;
                    spawnY = Math.Clamp(_state.PlayerY, 1, destZone.Height - 2);
                    edgeCheckX = 0;
                    edgeCheckY = spawnY;
                }
                else if (dy < 0)
                {
                    // Going up - spawn on bottom edge of dest
                    spawnX = Math.Clamp(_state.PlayerX, 1, destZone.Width - 2);
                    spawnY = destZone.Height - 2;
                    edgeCheckX = spawnX;
                    edgeCheckY = destZone.Height - 1;
                }
                else
                {
                    // Going down - spawn on top edge of dest
                    spawnX = Math.Clamp(_state.PlayerX, 1, destZone.Width - 2);
                    spawnY = 1;
                    edgeCheckX = spawnX;
                    edgeCheckY = 0;
                }

                // Check if the current zone's edge is walkable (player position edge tile)
                int currentEdgeX = Math.Clamp(_state.PlayerX, 0, _state.CurrentZone!.Width - 1);
                int currentEdgeY = Math.Clamp(_state.PlayerY, 0, _state.CurrentZone.Height - 1);
                if (!IsEdgeTileWalkable(_state.CurrentZone, currentEdgeX, currentEdgeY))
                {
                    Console.WriteLine($"Edge transition blocked: current zone edge ({currentEdgeX},{currentEdgeY}) not walkable");
                    _messages.ShowMessage("Can't go that way", MessageType.Info);
                    return;
                }

                // Check if destination zone's entry edge is walkable
                if (!IsEdgeTileWalkable(destZone, edgeCheckX, edgeCheckY))
                {
                    Console.WriteLine($"Edge transition blocked: dest zone {connectedZoneId} edge ({edgeCheckX},{edgeCheckY}) not walkable");
                    _messages.ShowMessage("Can't go that way", MessageType.Info);
                    return;
                }

                // One tile in is blocked: land on the edge tile itself, which was just checked.
                // The old fallback searched for the "nearest walkable" square, which could be a
                // pocket walled off from the edge you came through - trapping the player.
                if (!IsPositionWalkable(destZone, spawnX, spawnY))
                {
                    spawnX = edgeCheckX;
                    spawnY = edgeCheckY;
                }

                Console.WriteLine($"Edge transition {direction}: zone {_state.CurrentZoneId} -> {connectedZoneId} at ({spawnX},{spawnY})");
                _state.PreviousZoneId = _state.CurrentZoneId;
                LoadZone(connectedZoneId.Value, spawnX, spawnY);
                return;
            }
        }

        // Edge of zone with no transition
        _messages.ShowMessage("Can't go that way", MessageType.Info);
    }

    private bool IsExposedPickup(ZoneObject obj)
    {
        if (!obj.Enabled || obj.Argument == 0 || obj.Argument >= _gameData!.Tiles.Count ||
            _state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y) ||
            _state.CollectedObjects.Contains($"{_state.CurrentZoneId}_{obj.X}_{obj.Y}_tile")) return false;
        ushort tile = _state.CurrentZone!.GetTile(obj.X, obj.Y, 1);
        if (IsScriptedActorTile(_state.CurrentZone, obj.X, obj.Y)) return false;
        return tile == obj.Argument || _sectorPuzzles == null && tile == ushort.MaxValue;
    }

    private void CompleteHotspotPickup(ZoneObject obj)
    {
        _state.MarkObjectCollected(_state.CurrentZoneId, obj.X, obj.Y);
        _state.CollectedObjects.Add($"{_state.CurrentZoneId}_{obj.X}_{obj.Y}_tile");
        if (_state.CurrentZone!.GetTile(obj.X, obj.Y, 1) == obj.Argument)
            _state.CurrentZone.SetTile(obj.X, obj.Y, 1, ushort.MaxValue);
        _sectorPuzzles?.OnPickedUp(obj.Argument);
        SyncSectorMission();
    }

    private void CheckZoneObjects()
    {
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            if (!obj.Enabled)
                continue;

            if (obj.X != _state.PlayerX || obj.Y != _state.PlayerY)
                continue;

            switch (obj.Type)
            {
                case ZoneObjectType.CrateItem:
                    // Pick up item (if not already collected)
                    if (IsExposedPickup(obj))
                    {
                        _state.AddItem(obj.Argument);
                        CompleteHotspotPickup(obj);
                        var itemName = GetTileName(obj.Argument) ?? $"Item #{obj.Argument}";
                        _messages.ShowPickup(itemName);

                        // Check if picking up this item advances the mission
                        TryAdvanceMissionOnPickup(obj.Argument);
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    // Pick up weapon (if not already collected)
                    if (IsExposedPickup(obj))
                    {
                        // Walking over a pickup preserves the player's current selection.
                        CollectWeapon(obj.Argument);
                        CompleteHotspotPickup(obj);
                        var weaponName = GetTileName(obj.Argument) ?? $"Weapon";
                        var ammoState = _state.GetWeaponAmmo(obj.Argument);
                        if (ammoState != null)
                            _messages.ShowPickup($"{weaponName} ({ammoState.CurrentAmmo} ammo)");
                        else
                            _messages.ShowPickup(weaponName);
                        _messages.ShowMessage("Press Tab to switch weapons", MessageType.Info);
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
                        CompleteHotspotPickup(obj);
                        _state.HasLocator = true;
                        var itemName = GetTileName(obj.Argument) ?? "Locator Droid";
                        _messages.ShowPickup(itemName);
                        _messages.ShowMessage("Use the locator from your inventory for hints!", MessageType.Info);
                    }
                    break;

                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                    TravelThroughDoor(obj);
                    return;

                case ZoneObjectType.Teleporter:
                case ZoneObjectType.VehicleToSecondary:
                case ZoneObjectType.VehicleToPrimary:
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

                        // A vehicle lands on its counterpart (WebFun use-transport.ts).
                        bool byVehicle = obj.Type is ZoneObjectType.VehicleToSecondary or ZoneObjectType.VehicleToPrimary;
                        var counterpart = obj.Type == ZoneObjectType.VehicleToSecondary
                            ? ZoneObjectType.VehicleToPrimary : ZoneObjectType.VehicleToSecondary;
                        if (byVehicle && destZone.Objects.FirstOrDefault(o => o.Type == counterpart) is { } landing)
                            (spawnX, spawnY) = (landing.X, landing.Y);

                        // Look for a door that leads back to current zone, or a spawn point
                        foreach (var destObj in byVehicle && spawnX.HasValue ? Enumerable.Empty<ZoneObject>() : destZone.Objects)
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

                        LoadZone(destZoneId, spawnX, spawnY,
                            enteredByPlane: obj.Type is ZoneObjectType.VehicleToSecondary or ZoneObjectType.VehicleToPrimary);
                    }
                    break;

                case ZoneObjectType.XWingFromDagobah:
                case ZoneObjectType.XWingToDagobah:
                    TravelToPlanet(obj);
                    return;

                case ZoneObjectType.Trigger:
                    _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);
                    break;
            }
        }
    }

    private void UseItem() => UseItemAt(_state.SelectedItem, null, null, _state.CurrentZoneId);

    private void UseItemAt(int? selectedItem, int? targetX, int? targetY, int requestedZone)
    {
        if (MovementUIBlocked || _state.IsGameOver || _state.CurrentZoneId != requestedZone) return;
        if (_playerStep != null)
        {
            _pendingMovementAction = () => UseItemAt(selectedItem, targetX, targetY, requestedZone);
            return;
        }
        // If dialogue is active, dismiss it
        if (_messages.HasDialogue)
        {
            _messages.DismissDialogue();
            return;
        }

        if (_actionExecutor?.IsBusy == true || !_state.HeroVisible)
            return;

        // Scripts can consume items without going through RemoveItem.
        if (selectedItem.HasValue && !_state.HasItem(selectedItem.Value))
        {
            if (_state.SelectedItem == selectedItem) _state.SelectedItem = null;
            return;
        }

        if (selectedItem.HasValue)
        {
            var usedItemId = selectedItem.Value;

            if (IsLocatorTile(usedItemId))
            {
                PlayYodaSound(YodaLocator);
                _debugMapWindow?.Open(locatorMode: true);
                return;
            }
            if (usedItemId == WorldGenerator.TILE_LOCATOR)
            {
                ShowLocatorHint();
                return;
            }

            var facing = GetFacingPosition();
            int useX = targetX ?? facing.x, useY = targetY ?? facing.y;
            // A floor switch may be underneath Luke. Prefer the faced target,
            // but allow Space to address an authored target on his own tile.
            if (!targetX.HasValue && !HasItemUseTarget(useX, useY, usedItemId) && HasItemUseTarget(_state.PlayerX, _state.PlayerY, usedItemId))
                (useX, useY) = (_state.PlayerX, _state.PlayerY);
            if (_state.CurrentZone == null || useX < 0 || useY < 0 ||
                useX >= _state.CurrentZone.Width || useY >= _state.CurrentZone.Height ||
                Math.Abs(useX - _state.PlayerX) + Math.Abs(useY - _state.PlayerY) > 1)
            {
                _messages.ShowMessage("Move next to the target to use that item.", MessageType.Info);
                return;
            }

            // Set the placed item context for action conditions
            if (_actionExecutor != null)
            {
                _actionExecutor.PlacedItemId = usedItemId;
                (_actionExecutor.PlacedX, _actionExecutor.PlacedY) = (useX, useY);

                // Also set NPC context if there's a nearby friendly NPC
                var nearbyNpc = _state.ZoneNPCs.FirstOrDefault(n => n.IsEnabled && n.IsAlive && !n.IsHostile && n.X == useX && n.Y == useY);
                _actionExecutor.InteractingNpcId = nearbyNpc?.CharacterId;
                if (nearbyNpc != null)
                {
                    _actionExecutor.InteractingNpcId = nearbyNpc.CharacterId;
                    Console.WriteLine($"UseItem: Set InteractingNpcId to {nearbyNpc.CharacterId} for nearby NPC");

                    // Check if this advances the mission
                    if (_sectorPuzzles == null)
                        TryAdvanceMission(usedItemId, nearbyNpc);
                }
            }

            _actionExecutor?.ExecuteZoneActions(ActionTrigger.UseItem);

            // No script took the item: the sector's own puzzle gets a go (WebFun's PlaceTile
            // hotspot mode). A script that used the item has already removed it.
            bool scripted = _actionExecutor?.HandledItemPlacement == true;
            bool waiting = _actionExecutor?.IsBusy == true || _messages.HasDialogue || _actionExecutor?.RequestedZoneChange != null;
            bool sectorHandled = false;
            if (_sectorPuzzles != null && !scripted && !waiting && _state.HasItem(usedItemId))
                sectorHandled = _sectorPuzzles.PlaceItem(usedItemId, useX, useY);
            if (_sectorPuzzles != null && _state.CurrentZone != null)
            {
                _sectorPuzzles.Refresh(_state.CurrentZone);
                SyncSectorMission();
            }

            // Clear the context after execution
            if (_actionExecutor != null)
            {
                _actionExecutor.PlacedItemId = null;
                _actionExecutor.InteractingNpcId = null;
                _actionExecutor.PlacedX = _actionExecutor.PlacedY = -1;
            }
            if (!scripted && !sectorHandled && !waiting)
                _messages.ShowMessage("That item has no effect here.", MessageType.Info);
        }
        else
        {
            // Facing the sector's NPC or Marcus's desk: the action button bumps it
            var (facingX, facingY) = _state.PlayerDirection switch
            {
                Direction.Up => (_state.PlayerX, _state.PlayerY - 1),
                Direction.Down => (_state.PlayerX, _state.PlayerY + 1),
                Direction.Left => (_state.PlayerX - 1, _state.PlayerY),
                _ => (_state.PlayerX + 1, _state.PlayerY),
            };
            if (_sectorPuzzles?.Bump(facingX, facingY) == true)
            {
                SyncSectorMission();
                return;
            }

            // Pressing action at a tile is bumping it, as far as the zone's scripts are concerned
            if (_state.CurrentZone != null && _state.CurrentZone.GetTile(facingX, facingY, 1) != 0xFFFF)
                BumpInto(facingX, facingY);

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

    private bool HasItemUseTarget(int x, int y, int item)
    {
        var zone = _state.CurrentZone;
        if (zone == null) return false;
        if (zone.Objects.Any(h => h.Enabled && h.X == x && h.Y == y && (int)h.Type is 1 or 12)) return true;
        return _actionExecutor?.WouldHandleItemPlacement(item, x, y) == true;
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
        {
            // No more steps - mark mission as complete if not already
            if (!mission.IsCompleted)
            {
                Console.WriteLine($"Mission: All steps done, marking complete");
                mission.IsCompleted = true;
                _messages.ShowMessage("MISSION COMPLETE!", MessageType.System);
                _messages.ShowMessage(MissionCompleteHint, MessageType.Info);
                PlayYodaSound(YodaFlourish);
            }
            return;
        }

        // Check if the used item matches what's needed for the current step:
        // - Exact match: RequiredItemId == itemId
        // - Any item: RequiredItemId == 0 (step accepts any item)
        // - Item in chain: Check if it's in any step of the puzzle chain
        bool itemMatches = (currentStep.RequiredItemId == itemId) ||
                          (currentStep.RequiredItemId == 0 && itemId > 0);

        // Also accept if this item appears anywhere in the puzzle chain
        if (!itemMatches)
        {
            itemMatches = mission.PuzzleChain.Any(step =>
                step.RequiredItemId == itemId || step.RewardItemId == itemId);
        }

        if (itemMatches)
        {
            Console.WriteLine($"Mission: Used item {itemId} for step {mission.CurrentStep + 1} (required: {currentStep.RequiredItemId})");

            // Remove the used item from inventory
            _state.RemoveItem(itemId);
            var usedItemName = GetTileName(itemId) ?? "item";

            // Give the reward item if any
            if (currentStep.RewardItemId > 0)
            {
                _state.AddItem(currentStep.RewardItemId);
                var rewardName = GetTileName(currentStep.RewardItemId) ?? "item";
                _messages.ShowMessage($"Traded {usedItemName} for {rewardName}!", MessageType.Pickup);

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
                _messages.ShowMessage(MissionCompleteHint, MessageType.Info);
                PlayYodaSound(YodaFlourish);
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
    /// Checks if picking up an item advances the mission (e.g., finding a required item).
    /// </summary>
    private void TryAdvanceMissionOnPickup(int itemId)
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

        // Check if the picked up item is the reward for a "find item" step (RequiredItemId == 0)
        // Or if it matches the reward of the current step
        if (currentStep.RequiredItemId == 0 && currentStep.RewardItemId == itemId)
        {
            Console.WriteLine($"Mission: Found reward item {itemId} for step {mission.CurrentStep + 1}");

            // Advance the mission
            bool missionComplete = world.AdvanceMission();

            if (missionComplete)
            {
                _messages.ShowMessage("MISSION COMPLETE!", MessageType.System);
                _messages.ShowMessage(MissionCompleteHint, MessageType.Info);
                PlayYodaSound(YodaFlourish);
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
            if (!obj.Enabled || obj.X != targetX || obj.Y != targetY)
                continue;

            switch (obj.Type)
            {
                case ZoneObjectType.CrateItem:
                    if (IsExposedPickup(obj))
                    {
                        _state.AddItem(obj.Argument);
                        CompleteHotspotPickup(obj);
                        var itemName = GetTileName(obj.Argument) ?? $"Item";
                        _messages.ShowPickup(itemName);
                        return true;
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    if (IsExposedPickup(obj))
                    {
                        CollectWeapon(obj.Argument);
                        EquipWeapon(obj.Argument);
                        CompleteHotspotPickup(obj);
                        var weaponName = GetTileName(obj.Argument) ?? $"Weapon";
                        var ammoState = _state.GetWeaponAmmo(obj.Argument);
                        if (ammoState != null)
                            _messages.ShowPickup($"{weaponName} ({ammoState.CurrentAmmo} ammo)");
                        else
                            _messages.ShowPickup(weaponName);
                        _messages.ShowMessage("Press Tab to switch weapons", MessageType.Info);
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
                        CompleteHotspotPickup(obj);
                        _state.HasLocator = true;
                        var locatorName = GetTileName(obj.Argument) ?? "Locator Droid";
                        _messages.ShowPickup(locatorName);
                        _messages.ShowMessage("Use the locator from your inventory for hints!", MessageType.Info);
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

            // Talking must not intercept an attack aimed away from a nearby friendly.
            bool atTarget = npc.X == targetX && npc.Y == targetY;
            if (atTarget)
            {
                // Hostile NPCs can't be talked to - let the attack handle them
                if (npc.IsHostile)
                    return false;  // Fall through to attack

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
                            // This mission has already been acknowledged by Yoda or its win script.
                            if (_state.IsGameWon) return true;

                            // Mission complete! Increment counter
                            _state.GamesWon++;
                            PlayYodaSound(YodaYouWin);

                            // Check for 15-mission cycle milestones
                            if (_state.GamesWon >= 15)
                            {
                                // All 15 missions complete! Show victory screen and score
                                _state.IsGameWon = true;
                                _messages.ShowDialogue("Yoda", "A true Jedi, you have become! All 15 missions completed. Proud of you, I am.");
                                _messages.ShowMessage("CONGRATULATIONS! You've completed all 15 missions!", MessageType.System);
                                _messages.ShowMessage("Press R to start a new 15-mission cycle.", MessageType.Info);

                                // Calculate and save high score
                                var (total, _, _, _, _) = _state.CalculateScore();
                                var elapsedTime = DateTime.Now - _state.GameStartTime;
                                string rating = GetScoreRating(total, _gameData!.GameType);
                                HighScoreManager.AddScore(_gameData!.GameType, total, rating, _state.WorldSize, elapsedTime);

                                // Show score window
                                _scoreWindow?.Show(_state, _gameData!.GameType);
                            }
                            else if (_state.GamesWon == 10)
                            {
                                // Give The Force upgrade at mission 10
                                UpgradeWeapon(TILE_THE_FORCE);
                                _messages.ShowDialogue("Yoda", "Completed 10 missions, you have! Master of the Force, you are becoming. This power, I give to you.");
                                _messages.ShowMessage("Received: The Force! You can now attack from a distance.", MessageType.Pickup);
                                _messages.ShowMessage($"Mission {_state.GamesWon}/15 complete. Press R to continue.", MessageType.System);
                            }
                            else if (_state.GamesWon == 5)
                            {
                                // Give upgraded lightsaber at mission 5
                                UpgradeWeapon(TILE_UPGRADED_LIGHTSABER);
                                _messages.ShowDialogue("Yoda", "Completed 5 missions, you have! Growing stronger, you are. A better lightsaber, you have earned.");
                                _messages.ShowMessage("Received: Upgraded Lightsaber!", MessageType.Pickup);
                                _messages.ShowMessage($"Mission {_state.GamesWon}/15 complete. Press R to continue.", MessageType.System);
                            }
                            else
                            {
                                // Regular mission complete
                                _messages.ShowDialogue("Yoda", $"Completed the mission, you have! Strong with the Force, you are. {_state.GamesWon}/15 missions completed.");
                                _messages.ShowMessage($"Mission {_state.GamesWon}/15 complete. Press R to continue.", MessageType.System);
                            }
                            _state.IsGameWon = true;  // Allows pressing R to restart
                        }
                        // Give the starting item if player doesn't have it yet
                        else if (world.StartingItemId.HasValue && !_state.HasItem(world.StartingItemId.Value))
                        {
                            Console.WriteLine($"Yoda giving item: {world.StartingItemId.Value}");
                            _state.AddItem(world.StartingItemId.Value);
                            var itemName = GetTileName(world.StartingItemId.Value) ?? "an item";

                            // Puzzle string 0 is only the mission title. String 3 is the
                            // full briefing used by the original mission handoff.
                            var goalPuzzle = mission?.GoalPuzzle;
                            var missionText = goalPuzzle != null && goalPuzzle.Strings.Count > 3
                                ? goalPuzzle.Strings[3].Trim()
                                : "";
                            if (string.IsNullOrWhiteSpace(missionText))
                                missionText = mission?.Description ?? "";
                            if (string.IsNullOrWhiteSpace(missionText))
                                missionText = mission?.Name ?? "";

                            // Show the full mission briefing, then identify the item Yoda gives.
                            var briefing = mission != null
                                ? $"A mission for you, I have. {mission.Name}\n\n{missionText}\n\nTake this {itemName}. To {mission.Planet}, you must go. Find your X-Wing nearby."
                                : $"Take this {itemName}. Your journey begins. Find your X-Wing nearby.";
                            _messages.ShowDialogue("Yoda", briefing);
                            _messages.ShowMessage($"Received: {itemName}", MessageType.Pickup);

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
                    // Check if NPC is R2D2 (by name containing "r2")
                    else if (IsR2D2Character(npc.CharacterId) && !_state.HasLocator)
                    {
                        if (!npc.IsReadyToCollect)
                        {
                            // First interaction - R2D2 starts flashing, ready to collect
                            npc.IsReadyToCollect = true;
                            _messages.ShowDialogue("R2-D2", "*beep boop whistle* I will join you on your mission!");
                            _messages.ShowMessage("Walk over R2-D2 to add it to your inventory.", MessageType.Info);
                            Console.WriteLine($"R2D2 at ({npc.X},{npc.Y}) is now ready to collect");
                        }
                        else
                        {
                            // Already ready - remind player to walk over
                            _messages.ShowDialogue("R2-D2", "*excited beeps* Walk over me to pick me up!");
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

                        Console.WriteLine($"NPC {npcName} gave item {itemId} ({itemName}) to player");
                    }
                    else
                    {
                        // Do not invent dialogue for an actor without an authored interaction.
                        return false;
                    }
                }
                return true;
            }
        }

        return false;
    }

    private void PerformAttack()
    {
        if (_smoothMovement && (MovementUIBlocked || _state.IsGameOver)) return;
        if (_playerStep != null) { _pendingMovementAction = () => PerformAttack(); return; }
        if (_actionExecutor?.IsBusy == true || !_state.HeroVisible)
            return;

        // Determine weapon type
        bool isRanged = false;
        if (_state.SelectedWeapon.HasValue && _state.SelectedWeapon.Value > 0)
        {
            // Check if it's The Force (special ranged weapon)
            if (_state.SelectedWeapon.Value == TILE_THE_FORCE)
            {
                isRanged = true;
            }
            // Check tile flags for blasters
            else if (_state.SelectedWeapon.Value < _gameData!.Tiles.Count)
            {
                var weaponTile = _gameData.Tiles[_state.SelectedWeapon.Value];
                isRanged = (weaponTile.Flags & (TileFlags.WeaponLightBlaster | TileFlags.WeaponHeavyBlaster)) != 0;
            }
        }

        if (isRanged)
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
        if (!_state.SelectedWeapon.HasValue)
            return;

        int weaponId = _state.SelectedWeapon.Value;
        var ammoState = _state.GetWeaponAmmo(weaponId);

        // Check and consume ammo (if weapon has limited ammo)
        if (ammoState != null)
        {
            if (ammoState.CurrentAmmo <= 0)
            {
                _messages.ShowMessage("Out of ammo!", MessageType.Combat);
                // Switch to melee or next weapon
                if (_state.Weapons.Count > 1)
                {
                    CycleWeapon();
                    _messages.ShowMessage("Switched weapons", MessageType.Info);
                }
                return;
            }

            // Consume ammo
            _state.ConsumeWeaponAmmo(weaponId);

            // Show ammo count
            if (ammoState.CurrentAmmo > 0)
                _messages.ShowMessage($"*pew* ({ammoState.CurrentAmmo} left)", MessageType.Combat);
            else
                _messages.ShowMessage("*pew* (last shot!)", MessageType.Combat);
        }
        else
        {
            // Unlimited ammo (The Force, etc)
            _messages.ShowMessage("*pew*", MessageType.Combat);
        }

        // Trigger attack animation
        _state.IsAttacking = true;
        _state.AttackTimer = _attackAnimationDuration = 0.15;  // Shorter for shooting

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

        // Get damage from ammo state or use default
        int damage = ammoState?.Damage ?? 200;

        // Determine projectile type
        var projType = ProjectileType.Blaster;
        if (weaponId == TILE_THE_FORCE)
            projType = ProjectileType.Force;
        else if (_gameData != null && weaponId < _gameData.Tiles.Count)
        {
            var tile = _gameData.Tiles[weaponId];
            if ((tile.Flags & TileFlags.WeaponHeavyBlaster) != 0)
                projType = ProjectileType.HeavyBlaster;
        }

        // Spawn projectile
        var projectile = new Projectile
        {
            X = _state.PlayerX + (velX > 0 ? 0.5 : velX < 0 ? -0.5 : 0),
            Y = _state.PlayerY + (velY > 0 ? 0.5 : velY < 0 ? -0.5 : 0),
            VelocityX = velX,
            VelocityY = velY,
            Damage = damage,
            LifeTime = 2.0,
            Type = projType
        };
        _state.Projectiles.Add(projectile);

        if (_state.SelectedWeapon.HasValue)
            PlayWeaponSound(WeaponCharacterFor(_state.SelectedWeapon.Value));

        // Handle single-use weapons (grenades, thermal detonators)
        if (ammoState is { IsSingleUse: true, CurrentAmmo: 0 })
        {
            var weaponName = GetTileName(weaponId);
            _messages.ShowMessage($"{weaponName} used up!", MessageType.Info);
            _state.RemoveDepletedWeapon(weaponId);
        }
        // Check if weapon is now empty
        else if (ammoState != null && ammoState.CurrentAmmo == 0)
        {
            var weaponName = GetTileName(weaponId);
            _messages.ShowMessage($"{weaponName} is empty!", MessageType.Combat);
            _state.RemoveDepletedWeapon(weaponId);
        }
    }

    /// <summary>
    /// Cycles to the next weapon.
    /// </summary>
    private void CycleWeapon()
    {
        if (_state.Weapons.Count == 0)
        {
            _state.SelectedWeapon = null;
            return;
        }

        _state.CurrentWeaponIndex = (_state.CurrentWeaponIndex + 1) % _state.Weapons.Count;
        EquipWeapon(_state.Weapons[_state.CurrentWeaponIndex]);
    }

    private void PerformMeleeAttack()
    {
        if (_state.SelectedWeapon.HasValue)
            PlayWeaponSound(WeaponCharacterFor(_state.SelectedWeapon.Value));

        // Trigger attack animation
        _state.IsAttacking = true;
        _state.AttackTimer = _attackAnimationDuration = 0.3;

        // Calculate primary attack position based on facing direction
        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (_state.PlayerDirection)
        {
            case Direction.Up: targetY--; break;
            case Direction.Down: targetY++; break;
            case Direction.Left: targetX--; break;
            case Direction.Right: targetX++; break;
        }

        // 3-tile spread pattern: primary target + 2 diagonal offsets
        // Example facing Right: (x+1,y), (x+1,y-1), (x+1,y+1)
        var attackPositions = new List<(int X, int Y)> { (targetX, targetY) };
        switch (_state.PlayerDirection)
        {
            case Direction.Up:
            case Direction.Down:
                attackPositions.Add((targetX - 1, targetY));
                attackPositions.Add((targetX + 1, targetY));
                break;
            case Direction.Left:
            case Direction.Right:
                attackPositions.Add((targetX, targetY - 1));
                attackPositions.Add((targetX, targetY + 1));
                break;
        }

        Console.WriteLine($"Melee attack at ({targetX},{targetY}) +spread, {_state.ZoneNPCs.Count} NPCs in zone");

        // Calculate damage - scaled for 768 HP system
        int damage = _state.SelectedWeapon.HasValue ? 200 : 100;

        bool hitAny = false;
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            // Check all 3 attack positions
            bool inRange = false;
            foreach (var pos in attackPositions)
            {
                var position = NPCRenderPosition(npc);
                if (_smoothMovement ? (int)Math.Floor(position.X + 0.5) == pos.X && (int)Math.Floor(position.Y + 0.5) == pos.Y
                    : npc.X == pos.X && npc.Y == pos.Y)
                {
                    inRange = true;
                    break;
                }
            }

            if (!inRange)
                continue;

            hitAny = true;
            bool killed = npc.TakeDamage(damage);
            _state.AttackFlashTimer = 0.5;

            if (killed)
            {
                HandleNPCDeath(npc);
            }
            else
            {
                _messages.ShowCombat($"Hit! {npc.Health} HP left");
                Console.WriteLine($"  Hit NPC for {damage} damage, {npc.Health} HP left");
            }
        }

        if (hitAny)
        {
            _actionExecutor?.ExecuteZoneActions(ActionTrigger.Attack);
        }
        else
        {
            // No NPC found - show swing/miss feedback
            _state.AttackFlashTimer = 0.2;
            _messages.ShowMessage("*swing*", MessageType.Combat);
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
            // An NPC hotspot's argument is a tile to draw, not a character. Sector worlds lay
            // it down as one (SectorPuzzles); spawning it as a character is how a tile number
            // turned into Indiana Jones' "Rock".
            if (obj.Type == ZoneObjectType.PuzzleNPC && _sectorPuzzles == null)
            {
                var npc = NPC.FromZoneObject(obj);
                ConfigureNPCFromCharacter(npc);

                _state.ZoneNPCs.Add(npc);
            }
        }

        // 2. Spawn the zone's IZAX monsters
        {
            foreach (var entity in _state.CurrentZone.Monsters)
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
                    // Loot is stored as tile + 1, so 0 and -1 both mean "nothing of its own"
                    CarriedItemId = entity.Loot > 0 ? entity.Loot - 1 : null,
                };

                ConfigureNPCFromCharacter(npc);

                // IZAX explicitly controls death drops. Loot zero/-1 selects the
                // generated quest item; it does not mean this enemy drops nothing.
                npc.DropsLoot = entity.DropsLoot;
                npc.LootItemId = entity.Loot > 0 ? entity.Loot - 1 : -1;

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
            if (character.AuxData != null && character.AuxData.Damage >= 0)
            {
                npc.Damage = character.AuxData.Damage;
            }

            // Apply character weapon data for health (ignore invalid/placeholder values like 0xFFFF)
            if (character.Weapon != null && character.Weapon.Health > 0 && character.Weapon.Health < 1000)
            {
                npc.MaxHealth = character.Weapon.Health;
                npc.Health = character.Weapon.Health;
            }

            // An enemy's CHWP reference is the weapon character it fires (a Stormtrooper's
            // Imperial Blaster, a Nazi's pistol); no weapon means melee only.
            if (character.Weapon != null && character.Weapon.Reference >= 0 &&
                character.Weapon.Reference < _gameData!.Characters.Count)
            {
                npc.HasRangedAttack = true;
                npc.WeaponCharacterId = character.Weapon.Reference;
            }

            // Enemy NPCs drop loot from their carried item
            if (character.Type == CharacterType.Enemy && npc.CarriedItemId.HasValue)
            {
                npc.DropsLoot = true;
                npc.LootItemId = npc.CarriedItemId.Value;
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
    /// Spawns R2D2 as an NPC in ANY Dagobah zone.
    /// R2D2 must be talked to, then walked over to collect.
    /// </summary>
    private void SpawnR2D2IfNeeded(int zoneId)
    {
        // Don't spawn if player already has the locator
        if (_state.HasLocator)
            return;

        if (_worldGenerator?.CurrentWorld == null)
            return;

        // Only spawn R2D2 in Dagobah zones
        if (!_worldGenerator.CurrentWorld.DagobahZones.Contains(zoneId))
        {
            Console.WriteLine($"  R2D2: Zone {zoneId} is not a Dagobah zone");
            return;
        }

        // Check if R2D2 is already spawned in this zone
        foreach (var npc in _state.ZoneNPCs)
        {
            if (IsR2D2Character(npc.CharacterId))
            {
                Console.WriteLine($"  R2D2: Already spawned in this zone at ({npc.X},{npc.Y})");
                return;
            }
        }

        var zone = _state.CurrentZone;
        if (zone == null) return;

        // Find a walkable position for R2D2 near player start
        int targetX = _state.PlayerX + 2;
        int targetY = _state.PlayerY;
        var (finalX, finalY) = FindWalkablePosition(zone, targetX, targetY);

        Console.WriteLine($"  *** SPAWNING R2D2 at ({finalX}, {finalY}) in Dagobah zone {zoneId} ***");

        // Create R2D2 as a friendly NPC - Character 61 is "R2Unit"
        const int R2D2_CHARACTER_ID = 61;
        var r2d2 = new NPC
        {
            CharacterId = R2D2_CHARACTER_ID, // Character 61 = R2Unit
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
        _state.ZoneNPCs.Add(r2d2);
        Console.WriteLine($"  R2D2 spawned! ZoneNPCs now has {_state.ZoneNPCs.Count} NPCs");
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

        if (IsGroundBlocked(zone, x, y)) return false;
        if (IsScriptedActorTile(zone, x, y)) return false;

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
    /// Checks if an edge tile (at zone boundary) is walkable for zone transitions.
    /// Edge tiles are at x=0, y=0, x=width-1, or y=height-1.
    /// Returns true if the tile allows passage (not a wall/blocking object).
    /// </summary>
    private bool IsEdgeTileWalkable(Zone zone, int x, int y)
    {
        // Must be within zone bounds
        if (x < 0 || x >= zone.Width || y < 0 || y >= zone.Height)
            return false;

        if (IsGroundBlocked(zone, x, y)) return false;
        if (IsScriptedActorTile(zone, x, y)) return false;

        // Check layer 1 (middle layer) for blocking objects - this is where walls/objects are
        var middleTileId = zone.GetTile(x, y, 1);
        if (middleTileId != 0xFFFF && middleTileId < _gameData!.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTileId];

            // Solid objects that aren't floors block passage
            if (tile.IsObject && !tile.IsFloor)
            {
                return false;
            }

            // MapWall flag indicates a wall tile
            if ((tile.Flags & TileFlags.MapWall) != 0)
            {
                return false;
            }
        }

        // No blocking tiles found - edge is walkable
        return true;
    }

    /// <summary>
    /// Checks for X-Wing and spawns it as a visible object.
    /// </summary>
    private void CheckForXWing(int zoneId)
    {
        _state.XWingPosition = null;
        if (_worldGenerator?.CurrentWorld == null) return;

        // X-Wing is available in all Dagobah zones
        if (_worldGenerator.CurrentWorld.DagobahZones.Contains(zoneId))
        {
            _state.SetVariable(999, 1);  // X-Wing available
            Console.WriteLine($"  X-Wing available (press X anywhere to travel)");

            // The source data has several X-Wing hotspots at the same coordinates, one
            // for each possible landing zone. Enable only this world's destination.
            if (_state.CurrentZone != null)
            {
                var xwingHotspots = _state.CurrentZone.Objects
                    .Where(obj => obj.Type == ZoneObjectType.XWingFromDagobah)
                    .ToList();
                var landingZoneId = _worldGenerator.CurrentWorld.LandingZoneId;
                var activeHotspot = xwingHotspots.FirstOrDefault(obj => obj.Argument == landingZoneId)
                    ?? xwingHotspots.FirstOrDefault(obj => obj.FileArgument == landingZoneId)
                    ?? xwingHotspots.FirstOrDefault();

                // These hotspots belong to the shared parsed game data. Keep every
                // destination entry intact so the next generated mission can choose a
                // different one; only toggle which one is active in this world.
                foreach (var hotspot in xwingHotspots)
                    hotspot.Enabled = ReferenceEquals(hotspot, activeHotspot);

                if (activeHotspot != null)
                {
                    Console.WriteLine($"  X-Wing location at ({activeHotspot.X}, {activeHotspot.Y})");
                    _state.XWingPosition = (activeHotspot.X, activeHotspot.Y);
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
    private void TravelToPlanet(ZoneObject? departureHotspot = null)
    {
        if (_smoothMovement && (MovementUIBlocked || _state.IsGameOver)) return;
        if (_playerStep != null) { _pendingMovementAction = () => TravelToPlanet(departureHotspot); return; }
        if (_worldGenerator?.CurrentWorld == null) return;
        if (_actionExecutor?.IsBusy == true) return;

        var world = _worldGenerator.CurrentWorld;
        departureHotspot ??= _state.CurrentZone?.Objects.FirstOrDefault(obj => obj.Enabled &&
            obj.Type is ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah);
        // The capture mission also departs from a medical station outside Dagobah.
        // The hotspot's direction applies there just as it does on the main planet.
        bool onDagobah = departureHotspot != null
            ? departureHotspot.Type == ZoneObjectType.XWingFromDagobah
            : world.DagobahZones.Contains(_state.CurrentZoneId);

        if (onDagobah)
        {
            // Travel FROM Dagobah TO mission planet
            var landingZoneId = world.LandingZoneId;
            if (landingZoneId > 0 && landingZoneId < _gameData!.Zones.Count)
            {
                var destZone = _gameData.Zones[landingZoneId];
                _messages.ShowMessage($"Traveling to {world.Planet}...", MessageType.System);
                PlayYodaSound(YodaXWingOut);

                // Find X-Wing landing spot - Luke must spawn AT the X-Wing
                int spawnX = destZone.Width / 2;
                int spawnY = destZone.Height / 2;

                // First priority: X-Wing landing spot
                var xwingObj = destZone.Objects.FirstOrDefault(o => o.Type == ZoneObjectType.XWingToDagobah);
                if (xwingObj != null)
                {
                    // A previous zone script or mission may have disabled this shared
                    // hotspot. Boarding must remain available in each newly generated world.
                    xwingObj.Enabled = true;
                    spawnX = xwingObj.X;
                    // This is a transit marker, not the cockpit. Landing scripts use
                    // this exact position to decide which arrival sequence to play.
                    spawnY = xwingObj.Y;
                    Console.WriteLine($"Landing at X-Wing position ({xwingObj.X},{xwingObj.Y}), Luke spawns at ({spawnX},{spawnY})");
                }
                else
                {
                    // Fallback: use spawn location
                    var spawnObj = destZone.Objects.FirstOrDefault(o => o.Type == ZoneObjectType.SpawnLocation);
                    if (spawnObj != null)
                    {
                        spawnX = spawnObj.X;
                        spawnY = spawnObj.Y;
                    }
                    Console.WriteLine($"WARNING: No X-Wing in landing zone, using fallback spawn ({spawnX},{spawnY})");
                }

                ClearXWingTiles(_state.CurrentZone!);
                LoadZone(landingZoneId, spawnX, spawnY, enteredByPlane: true);
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
                PlayYodaSound(YodaXWingOut);

                // Find spawn point near X-Wing
                int spawnX = destZone.Width / 2;
                int spawnY = destZone.Height / 2;
                foreach (var obj in destZone.Objects)
                {
                    if (obj.Type == ZoneObjectType.XWingFromDagobah)
                    {
                        spawnX = obj.X;
                        spawnY = obj.Y + 2;  // Dagobah's arrival script expects (6,12).
                        break;
                    }
                    if (obj.Type == ZoneObjectType.SpawnLocation)
                    {
                        spawnX = obj.X;
                        spawnY = obj.Y;
                    }
                }

                ClearXWingTiles(_state.CurrentZone!);
                LoadZone(dagobahZoneId, spawnX, spawnY, enteredByPlane: true);
            }
        }
    }

    // The eight native ship tiles belong to the landing/takeoff scripts. Remove
    // fragments from a shortcut or interrupted flight before starting a new landing.
    private static void ClearXWingTiles(Zone zone)
    {
        for (int y = 0; y < zone.Height; y++)
        for (int x = 0; x < zone.Width; x++)
        for (int layer = 1; layer < 3; layer++)
            if (zone.GetTile(x, y, layer) is >= 948 and <= 955)
                zone.SetTile(x, y, layer, ushort.MaxValue);
    }

    private void PrepareXWingLanding(Zone zone)
    {
        // Identify the native landing animation by its ship drawing and Hide/ShowHero
        // pair. Its counter/variable guards describe the flight state for arrival.
        var landing = zone.Actions.FirstOrDefault(action =>
            action.Conditions.Any(c => c.Opcode == ConditionOpcode.EnterByPlane) &&
            action.Instructions.Any(i => i.Opcode == InstructionOpcode.HideHero) &&
            action.Instructions.Any(i => i.Opcode == InstructionOpcode.ShowHero) &&
            action.Instructions.Any(i => i.Opcode == InstructionOpcode.PlaceTile &&
                i.Arguments.Count >= 4 && i.Arguments[3] is >= 948 and <= 955));
        if (landing == null)
            return;

        ClearXWingTiles(zone);
        foreach (var condition in landing.Conditions)
        {
            var args = condition.Arguments;
            if (condition.Opcode == ConditionOpcode.CounterIs && args.Count >= 1)
                _state.SetCounter(zone.Id, args[0]);
            else if (condition.Opcode == ConditionOpcode.IsVariable && args.Count >= 4)
                zone.SetTile(args[1], args[2], args[3], (ushort)args[0]);
        }
    }

    /// <summary>Complete explicit zone changes and flight scripts' final transit-marker move.</summary>
    private bool CompleteScriptedTravel()
    {
        if (_actionExecutor?.RequestedZoneChange is { } requested)
        {
            bool arrivingByVehicle = !_state.HeroVisible;
            _actionExecutor.ResetForZone();
            LoadZone(requested.ZoneId, requested.X, requested.Y, arrivingByVehicle);
            return true;
        }

        if (_actionExecutor?.IsBusy == true || _state.CurrentZone == null)
            return false;

        var hotspot = _state.CurrentZone.Objects.FirstOrDefault(obj => obj.Enabled &&
            obj.X == _state.PlayerX && obj.Y == _state.PlayerY &&
            obj.Type is ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah);
        if (hotspot == null)
            return false;

        int previousZone = _state.CurrentZoneId;
        TravelToPlanet(hotspot);
        return previousZone != _state.CurrentZoneId;
    }

    private void Update(double deltaTime)
    {
        // Update palette animation (even during title screen for visual effects)
        if (Palette.UpdateAnimation(deltaTime))
        {
            // Palette colors changed - refresh animated tiles in the renderer
            _renderer?.RefreshAnimatedTiles();
        }

        if (_showingTitleScreen || _state.IsPaused || _debugMapWindow?.IsLocatorOpen == true || (_smoothMovement && MovementUIBlocked))
        {
            if (_smoothMovement) _suppressHeldMovement = true;
            return;
        }
        if (_discardMovementTime && _smoothMovement) { deltaTime = 0; _discardMovementTime = false; }
        // Dialogue timers must advance even while their script is suspended.
        _messages.Update(deltaTime);
        UpdateSmoothMovement(deltaTime);

        // The airfield's flight ends with a script putting the hero on the plane's square;
        // standing there is what takes off (WebFun HotspotExecutionMode.Stand).
        // (Checked once the speech that usually goes with it has been dismissed.)
        // Remembered rather than read off the hero, whom input can move before then.
        if (_actionExecutor?.PlacedOnVehicle is { } vehicle && !_messages.HasDialogue)
        {
            _actionExecutor.PlacedOnVehicle = null;
            (_state.PlayerX, _state.PlayerY) = (vehicle.X, vehicle.Y);
            CheckZoneObjects();
        }

        // Update animation
        _state.AnimationTimer += deltaTime;
        if (_state.AnimationTimer >= AnimationFrameTime)
        {
            _state.AnimationTimer -= AnimationFrameTime;
            _state.AnimationFrame = (_state.AnimationFrame + 1) % 3;

            // Sync NPC animations
            foreach (var npc in _state.ZoneNPCs)
            {
                npc.AnimationFrame = !_smoothMovement || npc.Behavior == NPCBehavior.Animation
                    ? _state.AnimationFrame
                    : _npcSteps.TryGetValue(npc, out var step) ? (int)(step.Elapsed / AnimationFrameTime) % 3 : 0;
            }
        }

        if (_playerWalkingTimer > 0)
        {
            _playerWalkingTimer = Math.Max(0, _playerWalkingTimer - deltaTime);
            _playerAnimationTimer += deltaTime;
            while (_playerAnimationTimer >= AnimationFrameTime)
            {
                _playerAnimationTimer -= AnimationFrameTime;
                _playerAnimationFrame = (_playerAnimationFrame + 1) % 3;
            }

            if (_playerWalkingTimer == 0)
            {
                _playerAnimationFrame = 0;
                _playerAnimationTimer = 0;
            }
        }
        else
        {
            _playerAnimationFrame = 0;
            _playerAnimationTimer = 0;
        }

        // First script evaluation (before NPC/projectile updates)
        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);
        if (CompleteScriptedTravel())
            return;
        if (_actionExecutor?.IsBusy == true)
        {
            if (_smoothMovement) CancelSmoothMovement();
            UpdateCamera();
            return;
        }

        // Update NPC AI
        UpdateNPCs(deltaTime);

        // Update projectiles
        UpdateProjectiles(deltaTime);

        // Second script evaluation (after NPC/projectile updates, before input)
        // This ensures scripts that depend on NPC state changes (like all-monsters-dead) trigger promptly
        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Walk);
        if (CompleteScriptedTravel())
            return;
        if (_actionExecutor?.IsBusy == true)
        {
            if (_smoothMovement) CancelSmoothMovement();
            UpdateCamera();
            return;
        }

        if (_state.CurrentZone != null) _sectorPuzzles?.Refresh(_state.CurrentZone);

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

            // Decrement stun timer - stunned NPCs skip all actions
            if (npc.StunTimer > 0)
            {
                npc.StunTimer--;
                continue;
            }

            // Hostile NPCs with ranged weapons try to shoot before moving
            if (npc.IsHostile && npc.HasRangedAttack && (!_smoothMovement || !_npcSteps.ContainsKey(npc)))
            {
                TryNPCRangedAttack(npc);
            }

            // Update move timer
            npc.MoveTimer += deltaTime;

            // Check if it's time to move
            if (npc.MoveTimer < npc.MoveCooldown || _smoothMovement && _npcSteps.ContainsKey(npc))
                continue;

            npc.MoveTimer = 0;

            if (_smoothMovement) StartSmoothNPCStep(npc);
            else switch (npc.Behavior)
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
                case NPCBehavior.Patrol:
                    UpdatePatrolNPC(npc);
                    break;
                case NPCBehavior.Animation:
                    // Animation-only NPCs just cycle frames (handled by global animation sync)
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

            // Hostile NPCs attack if adjacent to player (melee)
            if (npc.IsHostile)
            {
                npc.ActionTimer += deltaTime;
                if (npc.ActionTimer >= npc.AttackCooldown)
                {
                    var dist = npc.DistanceTo(_state.PlayerX, _state.PlayerY);
                    if (dist <= npc.AttackRange && (!_smoothMovement || _playerStep == null && !_npcSteps.ContainsKey(npc)))
                    {
                        npc.ActionTimer = 0;
                        _state.Health -= npc.Damage;
                        _state.DamageFlashTimer = 1.0;  // Trigger damage flash
                        _sounds?.PlaySound(SoundManager.Hurt);

                        var attackerName = GetCharacterName(npc.CharacterId) ?? "Enemy";
                        _messages.ShowCombat($"{attackerName} hits you! (-{npc.Damage} HP)");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Attempts a ranged attack from an NPC toward the hero.
    /// Monster checks if hero is within 4 tiles on same row/col, then rolls 1-in-7 chance to fire.
    /// </summary>
    private void TryNPCRangedAttack(NPC npc)
    {
        int dx = _state.PlayerX - npc.X;
        int dy = _state.PlayerY - npc.Y;

        // Must be on same row or column
        bool sameRow = dy == 0 && Math.Abs(dx) > 0 && Math.Abs(dx) <= 4;
        bool sameCol = dx == 0 && Math.Abs(dy) > 0 && Math.Abs(dy) <= 4;

        if (!sameRow && !sameCol)
            return;

        // The original rolls this once per monster tick, not once per rendered frame: at 60fps
        // every armed enemy in line emptied a magazine a second. Share melee's cooldown.
        if (npc.ActionTimer < npc.AttackCooldown)
            return;

        // ~85% chance to fire per eligible tick (rand() % 7 != 3 fires, so 6/7 ~ 85%)
        if (_random.Next(7) == 3)
            return;  // Miss this chance

        // Determine direction to player
        double velX = 0, velY = 0;
        const double npcProjectileSpeed = 8.0;  // Tiles per second (slower than player projectiles)

        if (sameRow)
        {
            velX = dx > 0 ? npcProjectileSpeed : -npcProjectileSpeed;
            npc.Direction = dx > 0 ? Direction.Right : Direction.Left;
        }
        else
        {
            velY = dy > 0 ? npcProjectileSpeed : -npcProjectileSpeed;
            npc.Direction = dy > 0 ? Direction.Down : Direction.Up;
        }

        var projectile = new Projectile
        {
            X = npc.X + (velX > 0 ? 0.5 : velX < 0 ? -0.5 : 0),
            Y = npc.Y + (velY > 0 ? 0.5 : velY < 0 ? -0.5 : 0),
            VelocityX = velX,
            VelocityY = velY,
            Damage = npc.Damage,
            LifeTime = 0.5,  // Max 4 tiles at speed 8 = 0.5s
            Type = ProjectileType.Blaster,
            IsEnemyProjectile = true,
        };
        _state.Projectiles.Add(projectile);
        npc.ActionTimer = 0;
        PlayWeaponSound(npc.WeaponCharacterId);
    }

    /// <summary>
    /// Updates a patrol NPC that follows waypoints.
    /// </summary>
    private void UpdatePatrolNPC(NPC npc)
    {
        if (npc.Waypoints.Length == 0)
        {
            // No waypoints - fall back to wandering
            UpdateWanderingNPC(npc);
            return;
        }

        var target = npc.Waypoints[npc.CurrentWaypoint];

        // Skip waypoints at (0,0) - unused slots
        if (target.X == 0 && target.Y == 0)
        {
            npc.CurrentWaypoint = (npc.CurrentWaypoint + 1) % npc.Waypoints.Length;
            return;
        }

        // Move toward current waypoint
        var dx = Math.Sign(target.X - npc.X);
        var dy = Math.Sign(target.Y - npc.Y);

        if (dx != 0)
        {
            npc.Direction = dx > 0 ? Direction.Right : Direction.Left;
            if (IsValidNPCPosition(npc.X + dx, npc.Y, npc))
            {
                npc.X += dx;
            }
        }
        else if (dy != 0)
        {
            npc.Direction = dy > 0 ? Direction.Down : Direction.Up;
            if (IsValidNPCPosition(npc.X, npc.Y + dy, npc))
            {
                npc.Y += dy;
            }
        }

        // Check if reached waypoint
        if (npc.X == target.X && npc.Y == target.Y)
        {
            npc.CurrentWaypoint = (npc.CurrentWaypoint + 1) % npc.Waypoints.Length;
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

            // Enemy projectiles check collision with hero
            if (projectile.IsActive && projectile.IsEnemyProjectile)
            {
                if (_smoothMovement ? ProjectileTouches(projectile, PlayerRenderPosition) : tileX == _state.PlayerX && tileY == _state.PlayerY)
                {
                    _state.Health -= projectile.Damage;
                    _state.DamageFlashTimer = 1.0;
                    _sounds?.PlaySound(SoundManager.Hurt);
                    _messages.ShowCombat($"Shot! (-{projectile.Damage} HP)");
                    projectile.IsActive = false;
                }
            }

            // Player projectiles check collision with NPCs
            if (projectile.IsActive && !projectile.IsEnemyProjectile)
            {
                foreach (var npc in _state.ZoneNPCs)
                {
                    if (!npc.IsEnabled || !npc.IsAlive)
                        continue;

                    if (_smoothMovement ? ProjectileTouches(projectile, NPCRenderPosition(npc)) : npc.X == tileX && npc.Y == tileY)
                    {
                        // The Force stuns instead of dealing damage
                        if (projectile.Type == ProjectileType.Force)
                        {
                            npc.StunTimer = projectile.Damage;
                            projectile.IsActive = false;
                            _state.AttackFlashTimer = 0.3;
                            var stunName = GetCharacterName(npc.CharacterId) ?? "Target";
                            _messages.ShowCombat($"{stunName} stunned!");
                        }
                        else
                        {
                            bool killed = npc.TakeDamage(projectile.Damage);
                            projectile.IsActive = false;
                            _state.AttackFlashTimer = 0.3;

                            var npcName = GetCharacterName(npc.CharacterId) ?? "Target";
                            if (killed)
                            {
                                HandleNPCDeath(npc);
                            }
                            else
                            {
                                _messages.ShowCombat($"Hit! {npc.Health} HP left");
                            }
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

    /// <summary>
    /// Handles NPC death: shows message, plays sound, drops loot.
    /// </summary>
    private void HandleNPCDeath(NPC npc)
    {
        // Release a defeated actor's reservations immediately, including mid-step deaths.
        _npcSteps.Remove(npc);
        var npcName = GetCharacterName(npc.CharacterId) ?? "Target";
        _messages.ShowCombat($"{npcName} defeated!");

        // Drop loot if applicable
        if (npc.DropsLoot && _state.CurrentZone != null)
        {
            int lootId = npc.LootItemId;

            // If loot is -1, use zone's DropQuestItem hotspot item
            if (lootId == -1 || lootId == 0xFFFF)
            {
                foreach (var obj in _state.CurrentZone.Objects)
                {
                    // Native DropQuestItem is hotspot zero, not the ordinary crate.
                    // It may be disabled: this enemy, rather than a floor pickup,
                    // owns the reward until it is defeated.
                    if ((int)obj.Type == 0 && obj.Argument != 0xFFFF && obj.Argument > 0)
                    {
                        lootId = obj.Argument;
                        break;
                    }
                }
            }

            if (lootId > 0 && lootId != 0xFFFF)
            {
                // Place loot item on object layer at NPC position
                _state.CurrentZone.SetTile(npc.X, npc.Y, 1, (ushort)lootId);
                var lootName = GetTileName(lootId);
                Console.WriteLine($"  Dropped loot: {lootName} (tile {lootId}) at ({npc.X},{npc.Y})");
                npc.DropsLoot = false;
            }
        }

        _actionExecutor?.ExecuteZoneActions(ActionTrigger.Attack);
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

        if (IsGroundBlocked(_state.CurrentZone, x, y)) return false;
        if (IsScriptedActorTile(_state.CurrentZone, x, y)) return false;

        // Check collision with player
        if (_smoothMovement && MovementReserved(x, y, npc)) return false;
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
            if (tile.IsSolid && !tile.IsDraggable && !IsNPCMapSprite(x, y, middleTile))
                return false;
        }

        return true;
    }

    private void Render()
    {
        if (_renderer == null)
            return;

        _renderer.BeginGameArea();

        // Render title screen
        if (_showingTitleScreen)
        {
            _titleScreen?.Render();
            RenderMenuBar();
            _renderer.Present();
            return;
        }

        if (_state.CurrentZone == null)
            return;

        var worldClip = new SDLRect { X = 0, Y = 0, W = GameRenderer.GameAreaWidth, H = GameRenderer.GameAreaHeight };
        SDL.RenderSetClipRect(_renderer.GetRenderer(), &worldClip);
        // Render zone
        _renderer.RenderZone(_state.CurrentZone, ViewCameraX, ViewCameraY, IsNPCMapSprite);

        // Render zone items (crates, weapons on ground)
        RenderZoneItems();

        // Render NPCs
        RenderNPCs();

        // Render player character
        RenderPlayer();

        // Authored weapon frames already contain the hero and blade. Only attacks
        // without a usable authored pose need the generic slash fallback.
        if (_state.IsAttacking && CurrentWeaponPose() == null)
        {
            var playerScreenX = (int)((PlayerRenderPosition.X - ViewCameraX) * Data.Tile.Width * GameRenderer.Scale);
            var playerScreenY = (int)((PlayerRenderPosition.Y - ViewCameraY) * Data.Tile.Height * GameRenderer.Scale);
            _renderer.RenderMeleeSlash(playerScreenX, playerScreenY, (int)_state.PlayerDirection,
                _state.AttackTimer / _attackAnimationDuration);
        }

        // Render projectiles
        RenderProjectiles();

        // Upper scenery occludes actors and effects beneath it.
        _renderer.RenderZoneRoof(_state.CurrentZone, ViewCameraX, ViewCameraY, IsNPCMapSprite);

        // DrawTileNow is a temporary surface operation, above the ordinary scene.
        if (_actionExecutor != null)
            foreach (var frame in _actionExecutor.DrawnTiles)
                _renderer.RenderSprite(frame.Value, frame.Key.X, frame.Key.Y, ViewCameraX, ViewCameraY);

        // Render script viewer highlights (if viewing current zone)
        if (_scriptViewer?.IsOpen == true)
        {
            var highlights = _scriptViewer.GetHighlightsForZone(_state.CurrentZoneId);
            _renderer.RenderHighlights(highlights, ViewCameraX, ViewCameraY);
        }

        SDL.RenderSetClipRect(_renderer.GetRenderer(), null);

        // Render HUD with weapon ammo info
        int currentAmmo = -1, maxAmmo = -1;
        if (_state.SelectedWeapon.HasValue)
        {
            var ammoState = _state.GetWeaponAmmo(_state.SelectedWeapon.Value);
            if (ammoState != null)
            {
                currentAmmo = ammoState.CurrentAmmo;
                maxAmmo = ammoState.MaxAmmo;
            }
        }
        _renderer.RenderHUD(_state.Health, _state.MaxHealth, _state.Inventory, _state.SelectedWeapon, _state.SelectedItem, currentAmmo, maxAmmo);

        // Render dragged item if dragging
        if (_isDragging && _draggedItemId.HasValue)
        {
            _renderer.RenderDraggedItem(_draggedItemId.Value, _dragCurrentX, _dragCurrentY);
        }

        // Render zone info
        _renderer.RenderZoneInfo(
            _state.CurrentZoneId,
            _state.CurrentZone.Name ?? _state.CurrentZone.Planet.ToString(),
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

        // Render debug overlay if visible
        if (_debugOverlay?.IsVisible == true)
        {
            var tabs = _debugOverlay.GetTabs();
            var lines = _debugOverlay.GetTabContent(_debugOverlay.CurrentTab);
            _renderer.RenderDebugOverlay(tabs, _debugOverlay.CurrentTab, lines, _debugOverlay.ScrollOffset);
        }

        _controllerOverlay?.Render(_renderer.GetRenderer());

        RenderMenuBar();

        // Present frame
        _renderer.Present();

        // Render debug map window (separate window)
        _debugMapWindow?.Render();
        _scriptViewer?.Render();
        _assetViewer?.Render();
        _saveInspector?.Render();
        _zoneEditor?.Render();
        _controlsWindow?.Render();
        _aboutWindow?.Render();
        _scoreWindow?.Render();
        _highScoreWindow?.Render();
    }

    /// <summary>
    /// Leaves the game area viewport and draws the menu bar in the strip above it.
    /// Both are no-ops on Windows, where the OS draws its own menu outside the window.
    /// </summary>
    private void RenderMenuBar()
    {
        _renderer!.EndGameArea();
        _menuBar?.Render();
    }

    /// <summary>
    /// Moves a mouse event from window coordinates into the game area, which starts below
    /// the menu bar strip. Everything downstream of the menu bar hit-tests against a layout
    /// that assumes the game starts at y=0, so this is the one place that knows otherwise.
    /// </summary>
    private static void ShiftPointerIntoGameArea(SDLEvent* evt)
    {
        if (Rendering.GameRenderer.MenuBarHeight == 0)
            return;

        switch ((SDLEventType)evt->Type)
        {
            case SDLEventType.Mousebuttondown:
            case SDLEventType.Mousebuttonup:
                evt->Button.Y -= (int)Rendering.GameRenderer.MenuBarHeight;
                break;
            case SDLEventType.Mousemotion:
                evt->Motion.Y -= (int)Rendering.GameRenderer.MenuBarHeight;
                break;
        }
    }

    private void RenderZoneItems()
    {
        // Generated worlds materialize rewards in the object layer only when uncovered.
        // Drawing hotspot arguments again exposes hidden and disabled alternatives.
        if (_sectorPuzzles != null) return;
        var drawn = new HashSet<(int X, int Y)>();
        foreach (var obj in _state.CurrentZone!.Objects)
        {
            // Only render item crates that haven't been picked up
            if (obj.Type != ZoneObjectType.CrateItem && obj.Type != ZoneObjectType.CrateWeapon)
                continue;

            if (!IsExposedPickup(obj) ||
                _state.CurrentZone.GetTile(obj.X, obj.Y, 1) != ushort.MaxValue ||
                !drawn.Add((obj.X, obj.Y))) continue;

            // Check if within viewport
            if (obj.X < ViewCameraX - 1 || obj.X >= ViewCameraX + GameRenderer.ViewportTilesX + 1 ||
                obj.Y < ViewCameraY - 1 || obj.Y >= ViewCameraY + GameRenderer.ViewportTilesY + 1)
                continue;

            // Render the item tile
            if (obj.Argument > 0 && obj.Argument < _gameData!.Tiles.Count)
            {
                _renderer!.RenderSprite(obj.Argument, obj.X, obj.Y, ViewCameraX, ViewCameraY);
            }
        }
    }

    private bool IsNPCMapSprite(int x, int y, ushort tile)
    {
        // Keep authored tiles available to scripts. Only suppress a matching sprite at
        // its IZAX spawn point; the live NPC renderer owns movement, visibility and death.
        foreach (var entity in _state.CurrentZone!.Monsters)
        {
            if (entity.X != x || entity.Y != y || entity.CharacterId >= _gameData!.Characters.Count)
                continue;
            var frames = _gameData.Characters[entity.CharacterId].Frames;
            if (frames.WalkUp.Contains(tile) || frames.WalkDown.Contains(tile) ||
                frames.WalkLeft.Contains(tile) || frames.WalkRight.Contains(tile)) return true;
        }
        return false;
    }

    private void RenderNPCs()
    {
        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive)
                continue;

            // Check if NPC is within viewport
            if (npc.X < ViewCameraX - 1 || npc.X >= ViewCameraX + GameRenderer.ViewportTilesX + 1 ||
                npc.Y < ViewCameraY - 1 || npc.Y >= ViewCameraY + GameRenderer.ViewportTilesY + 1)
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

                if (frames != null && frames.Length > 0)
                {
                    var tileId = GetUsableAnimationFrame(frames, npc.AnimationFrame);
                    if (tileId.HasValue)
                    {
                        _renderer!.RenderSprite(tileId.Value, NPCRenderPosition(npc).X, NPCRenderPosition(npc).Y, ViewCameraX, ViewCameraY);
                    }
                }
            }
            // If CharacterId is actually a tile ID (>= character count), render that tile directly
            else if (npc.CharacterId < _gameData!.Tiles.Count)
            {
                _renderer!.RenderSprite(npc.CharacterId, NPCRenderPosition(npc).X, NPCRenderPosition(npc).Y, ViewCameraX, ViewCameraY);
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
            if (tileX < ViewCameraX - 1 || tileX >= ViewCameraX + GameRenderer.ViewportTilesX + 1 ||
                tileY < ViewCameraY - 1 || tileY >= ViewCameraY + GameRenderer.ViewportTilesY + 1)
                continue;

            // Render projectile
            var screenX = (int)((projectile.X - ViewCameraX) * Data.Tile.Width * GameRenderer.Scale);
            var screenY = (int)((projectile.Y - ViewCameraY) * Data.Tile.Height * GameRenderer.Scale);
            _renderer!.RenderProjectile(screenX, screenY, projectile.Type);
        }
    }

    private void RenderPlayer()
    {
        // Skip rendering if hero is hidden (by HideHero script instruction)
        if (!_state.HeroVisible)
            return;

        if (CurrentWeaponPose() is { } attack)
        {
            var position = PlayerRenderPosition;
            _renderer!.RenderSprite(attack.Body, position.X, position.Y, ViewCameraX, ViewCameraY);
            if (attack.Extension is { } extension)
                _renderer.RenderSprite(extension, position.X + attack.OffsetX, position.Y + attack.OffsetY, ViewCameraX, ViewCameraY);
            return;
        }

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

            if (frames != null && frames.Length > 0)
            {
                var tileId = GetUsableAnimationFrame(frames, _playerAnimationFrame);
                if (tileId.HasValue)
                {
                    _renderer!.RenderSprite(tileId.Value, PlayerRenderPosition.X, PlayerRenderPosition.Y, ViewCameraX, ViewCameraY);
                    return;
                }
            }
        }

        // Fallback: render a placeholder tile
        if (_gameData.Tiles.Count > 800)
        {
            _renderer!.RenderSprite(800, PlayerRenderPosition.X, PlayerRenderPosition.Y, ViewCameraX, ViewCameraY);
        }
    }

    // Some CHAR records leave unused animation slots as tile 0 or 0xFFFF. Picking
    // those slots caused the character to disappear on every second/third step.
    private ushort? GetUsableAnimationFrame(ushort[] frames, int preferredFrame)
    {
        if (_gameData == null || frames.Length == 0)
            return null;

        preferredFrame = Math.Clamp(preferredFrame, 0, frames.Length - 1);
        for (int offset = 0; offset < frames.Length; offset++)
        {
            var tileId = frames[(preferredFrame + offset) % frames.Length];
            if (tileId > 0 && tileId < _gameData.Tiles.Count)
                return tileId;
        }

        return null;
    }

    private void ResetPlayerAnimation()
    {
        _playerWalkingTimer = 0;
        _playerAnimationTimer = 0;
        _playerAnimationFrame = 0;
    }

    /// <summary>
    /// Gets a tile name by ID, if available.
    /// </summary>
    private string GetTileName(int tileId)
    {
        // Check tile names dictionary first
        if (_gameData!.TileNames.TryGetValue(tileId, out var name))
            return name;

        // Check puzzles for associated strings
        if (_gameData.Puzzles != null)
        {
            foreach (var puzzle in _gameData.Puzzles)
            {
                if (puzzle.Item1 == tileId && puzzle.Strings.Count > 0)
                    return puzzle.Strings[0];
                if (puzzle.Item2 == tileId && puzzle.Strings.Count > 1)
                    return puzzle.Strings[1];
            }
        }

        // Fallback
        return $"Tile_{tileId}";
    }

    /// <summary>
    /// Checks the native map-item flags. R2 and other droid items are distinct.
    /// </summary>
    private bool IsLocatorTile(int tileId)
    {
        return _gameData != null && tileId >= 0 && tileId < _gameData.Tiles.Count &&
            (_gameData.Tiles[tileId].Flags & (TileFlags.Item | TileFlags.ItemLocator)) ==
            (TileFlags.Item | TileFlags.ItemLocator);
    }

    /// <summary>
    /// Checks if a character is R2-D2 (by tile ID or character name).
    /// </summary>
    private bool IsR2D2Character(int characterId)
    {
        // Character 61 is "R2Unit" - the R2D2 character
        const int R2D2_CHARACTER_ID = 61;
        if (characterId == R2D2_CHARACTER_ID)
            return true;

        // Legacy: also check tile 512 (TILE_LOCATOR) for backwards compatibility
        if (characterId == WorldGenerator.TILE_LOCATOR)
            return true;

        // Also check by character name if it's a valid character
        if (characterId >= 0 && characterId < _gameData!.Characters.Count)
        {
            var character = _gameData.Characters[characterId];
            var nameLower = character.Name.ToLowerInvariant();

            // Check for R2 variants in name
            if (nameLower.Contains("r2") ||
                nameLower.Contains("r-2") ||
                nameLower.Contains("artoo") ||
                nameLower.Contains("locator"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects R2D2 NPC, adding the locator to the player's inventory.
    /// </summary>
    private void CollectR2D2(NPC r2d2)
    {
        // Add locator item to inventory
        _state.AddItem(WorldGenerator.TILE_LOCATOR);
        _state.HasLocator = true;

        // Disable the NPC (collected)
        r2d2.IsEnabled = false;
        r2d2.Health = 0; // This makes IsAlive return false

        // Show pickup messages
        _messages.ShowDialogue("R2-D2", "*happy beeps* I'm ready to help with your mission!");
        _messages.ShowPickup("R2-D2 (Locator)");
        _messages.ShowMessage("Use the locator from your inventory for hints!", MessageType.Info);

        Console.WriteLine($"Collected R2D2 at ({r2d2.X},{r2d2.Y}), HasLocator={_state.HasLocator}");
    }

    /// <summary>
    /// Shows a hint from the locator/R2D2.
    /// Context-sensitive: checks what's at the target position and provides relevant hints.
    /// </summary>
    private void ShowLocatorHint()
    {
        PlayYodaSound(YodaLocator);

        // First check what's in front of the player for context-sensitive hints
        var targetPos = GetFacingPosition();
        string? contextHint = GetR2D2ContextHint(targetPos.x, targetPos.y);

        if (contextHint != null)
        {
            _messages.ShowDialogue("R2-D2", contextHint);
            return;
        }

        // Fall back to mission-based hints
        if (_worldGenerator?.CurrentWorld == null)
        {
            _messages.ShowDialogue("R2-D2", "*beep boop* No mission data available.");
            return;
        }

        var mission = _worldGenerator.CurrentWorld.Mission;
        if (mission == null)
        {
            _messages.ShowDialogue("R2-D2", GetGenericHint());
            return;
        }

        if (mission.IsCompleted)
        {
            _messages.ShowDialogue("R2-D2", "*happy beeps* Mission complete! Return to Yoda on Dagobah.");
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
    }

    /// <summary>
    /// Gets the position in front of the player.
    /// </summary>
    private (int x, int y) GetFacingPosition()
    {
        int x = _state.PlayerX;
        int y = _state.PlayerY;

        switch (_state.PlayerDirection)
        {
            case Direction.Up: y--; break;
            case Direction.Down: y++; break;
            case Direction.Left: x--; break;
            case Direction.Right: x++; break;
        }

        return (x, y);
    }

    /// <summary>
    /// Gets a context-sensitive hint based on what's at the given position.
    /// Returns null if no specific hint applies.
    /// </summary>
    private string? GetR2D2ContextHint(int x, int y)
    {
        if (_state.CurrentZone == null || _gameData == null) return null;
        if (x < 0 || x >= _state.CurrentZone.Width || y < 0 || y >= _state.CurrentZone.Height) return null;

        // Check for NPCs at position
        var npc = _state.ZoneNPCs.FirstOrDefault(n => n.IsEnabled && n.IsAlive && n.X == x && n.Y == y);
        if (npc != null)
        {
            if (npc.IsHostile)
                return "*warning beeps* Enemy detected! Use your weapon to defeat it.";
            else
            {
                var charName = _gameData.Characters.FirstOrDefault(c => c.Id == npc.CharacterId)?.Name ?? "This character";
                return $"*beep* {charName} appears friendly. Try talking to them - they may have useful information or items.";
            }
        }

        // Check for zone objects at position
        var obj = _state.CurrentZone.Objects.FirstOrDefault(o => o.X == x && o.Y == y);
        if (obj != null)
        {
            return obj.Type switch
            {
                ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit => "*beep boop* This door leads to another area. Walk into it to enter.",
                ZoneObjectType.Lock => "*concerned beeps* This is locked! You need to find the right item to open it.",
                ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon => "*excited beeps* There might be something useful here! Try opening it.",
                ZoneObjectType.LocatorItem => "*happy beeps* That's me! I can help you find your way.",
                ZoneObjectType.Trigger => "*beep* Something interesting is here. Step on it to see what happens.",
                ZoneObjectType.PuzzleNPC => "*beep* This character may be important for your mission. Try interacting with them.",
                ZoneObjectType.Teleporter => "*beep boop* This teleporter can transport you to another location.",
                _ => null
            };
        }

        // Check the tile at position
        var middleTile = _state.CurrentZone.GetTile(x, y, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];

            if (tile.IsDraggable)
                return "*beep* This object can be pushed or pulled. Try walking into it or holding shift to pull.";

            if (tile.IsWeapon)
                return "*beep boop* A weapon! Pick it up to defend yourself against enemies.";

            if (tile.IsItem)
            {
                var itemName = GetTileName(middleTile);
                return $"*beep* That's {itemName}. It might be useful for your mission.";
            }

            if ((tile.Flags & TileFlags.Object) != 0)
                return "*beep* An object is here. Try interacting with it.";
        }

        return null;
    }

    /// <summary>
    /// Gets a random generic hint when no specific context applies.
    /// </summary>
    private string GetGenericHint()
    {
        var hints = new[]
        {
            "*beep boop* Walk around and explore! There's always more to discover.",
            "*beep* Try talking to friendly characters - they often have useful items or information.",
            "*beep boop* Some objects can be pushed or pulled. Try approaching them from different directions.",
            "*beep* Use your weapon wisely! Attack enemies before they get too close.",
            "*beep boop* Keep an eye out for locked doors - you'll need to find the right item to open them."
        };

        return hints[new Random().Next(hints.Length)];
    }

    /// <summary>
    /// Gets the character index for a weapon tile.
    /// </summary>
    /// <summary>Runs the zone's bump scripts for walking (or pressing action) into (x, y).</summary>
    /// <summary>Somewhere the hero can step without pushing: in the zone, no object, no one standing there.</summary>
    private bool IsOpenFloor(int x, int y)
    {
        var zone = _state.CurrentZone;
        if (zone == null || x < 0 || y < 0 || x >= zone.Width || y >= zone.Height) return false;
        if (IsGroundBlocked(zone, x, y)) return false;
        var t = zone.GetTile(x, y, 1);
        if (t != 0xFFFF && (t >= _gameData!.Tiles.Count ||
            _gameData.Tiles[t].IsSolid && !IsNPCMapSprite(x, y, t))) return false;
        return !_state.ZoneNPCs.Any(n => n.IsEnabled && n.IsAlive && n.X == x && n.Y == y);
    }

    private void BumpInto(int x, int y)
    {
        if (_actionExecutor == null) return;
        _actionExecutor.BumpX = x;
        _actionExecutor.BumpY = y;
        _actionExecutor.ExecuteZoneActions(ActionTrigger.Bump);
        if (_sectorPuzzles != null && _state.CurrentZone != null)
            _sectorPuzzles.Refresh(_state.CurrentZone);
    }

    /// <summary>Moves the mission's current step to the first unsolved sector; announces the end once.</summary>
    private void SyncSectorMission()
    {
        if (_sectorPuzzles == null || _worldGenerator?.CurrentWorld?.Mission is not { IsCompleted: false } mission) return;
        if (!_sectorPuzzles.SyncMission()) return;
        mission.IsCompleted = true;
        _messages.ShowMessage("MISSION COMPLETE!", MessageType.System);
        _messages.ShowMessage(MissionCompleteHint, MessageType.Info);
    }

    private string MissionCompleteHint => _gameData?.GameType == GameType.IndianaJones
        ? $"You found the {GetTileName(_worldGenerator?.CurrentWorld?.Mission?.GoalPuzzle?.Item1 ?? -1) ?? "artifact"}!"
        : "Return to Yoda on Dagobah.";

    // Yoda Stories SNDS indices (WebFun variant/yoda/sounds.ts). Indiana Jones has its own
    // table, of which only Hurt and NoGo are known, so these play for Yoda only.
    private const int YodaLocator = 34;
    private const int YodaXWingOut = 39;
    private const int YodaFlourish = 58;
    private const int YodaYouWin = 63;

    private void PlayYodaSound(int soundId)
    {
        if (_gameData?.GameType != GameType.IndianaJones)
            _sounds?.PlaySound(soundId);
    }

    /// <summary>
    /// The weapon character equipped from inventory tile <paramref name="weaponTile"/>, or -1.
    /// A weapon character's first frame ends in that tile (WebFun: frames[0].extensionRight).
    /// </summary>
    private int WeaponCharacterFor(int weaponTile) =>
        _gameData == null ? -1 : WeaponAnimation.FindCharacter(_gameData, weaponTile);

    /// <summary>A weapon character's CHWP reference is the sound it makes when used.</summary>
    private void PlayWeaponSound(int weaponCharacterId)
    {
        if (_gameData != null && weaponCharacterId >= 0 && weaponCharacterId < _gameData.Characters.Count)
            _sounds?.PlaySound(_gameData.Characters[weaponCharacterId].Weapon?.Reference ?? -1);
    }

    /// <summary>WebFun's getEquipSound: Yoda picks by weapon, Indiana Jones always plays sound 0.</summary>
    private void PlayEquipSound(int weaponTile)
    {
        if (_gameData?.GameType == GameType.IndianaJones)
        {
            _sounds?.PlaySound(0);
            return;
        }

        _sounds?.PlaySound(weaponTile switch
        {
            512 or 513 => 32,                                         // Blaster, rifle: Armed
            TILE_BASIC_LIGHTSABER or TILE_UPGRADED_LIGHTSABER => 31,  // SaberOut
            TILE_THE_FORCE => 52,                                     // ArmForce
            _ => -1,
        });
    }

    private WeaponPose? CurrentWeaponPose() =>
        _state.IsAttacking && _state.SelectedWeapon.HasValue && _gameData != null
            ? WeaponAnimation.Resolve(_gameData, WeaponCharacterFor(_state.SelectedWeapon.Value), _state.PlayerDirection,
                Math.Clamp(1 - _state.AttackTimer / _attackAnimationDuration, 0, 1))
            : null;

    /// <summary>
    /// Checks if the given weapon is a ranged weapon (can attack from a distance).
    /// </summary>
    private bool IsRangedWeapon(int weaponTileId)
    {
        // The Force allows attacking from a distance
        return weaponTileId == TILE_THE_FORCE;
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

    /// <summary>
    /// Gets the rating string for a score.
    /// </summary>
    private string GetScoreRating(int score, Data.GameType gameType)
    {
        if (gameType == Data.GameType.IndianaJones)
        {
            if (score >= 450) return "Master Archaeologist";
            if (score >= 400) return "Professor";
            if (score >= 350) return "Seasoned Explorer";
            if (score >= 300) return "Field Researcher";
            if (score >= 250) return "Curator";
            if (score >= 200) return "Student";
            return "Amateur";
        }
        else
        {
            if (score >= 450) return "Legendary Hero";
            if (score >= 400) return "Jedi Master";
            if (score >= 350) return "Jedi Knight";
            if (score >= 300) return "Padawan";
            if (score >= 250) return "Force Sensitive";
            if (score >= 200) return "Adventurer";
            return "Beginner";
        }
    }

    public void Dispose()
    {
        // Clean up controller
        if (_controller != null)
        {
            SDL.GameControllerClose(_controller);
            _controller = null;
        }

        // Before the renderer: on Windows this puts the original WndProc back, and that
        // has to happen while the window it was taken from still exists.
        _menuBar?.Dispose();

        _debugMapWindow?.Dispose();
        _scriptViewer?.Dispose();
        _assetViewer?.Dispose();
        _highScoreWindow?.Dispose();
        _sounds?.Dispose();
        _renderer?.Dispose();
    }
}
