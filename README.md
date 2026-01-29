# Yoda Stories NG

A modern reimplementation of **Star Wars: Yoda Stories** (1997) built with C# and SDL2.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

## About

Yoda Stories NG is a fan-made recreation of the classic LucasArts desktop adventure game. It parses the original game data files and reimplements the game engine from scratch, featuring:

- **Complete data file parsing** - Reads original `.dta` game assets
- **Procedural world generation** - Each playthrough is unique
- **Mission system** - Quest chains with item trading and puzzle solving
- **Combat system** - Melee and ranged weapons with NPC AI
- **Widescreen UI** - Modern layout with sidebar HUD
- **Xbox controller support** - Full gamepad controls

## Screenshots

*Coming soon*

## Requirements

- .NET 8.0 SDK
- Original Yoda Stories game files (`yodesk.dta`)
- Windows, Linux, or macOS

## Building

```bash
# Clone the repository
git clone https://github.com/yourusername/YodaStoriesNG.git
cd YodaStoriesNG

# Build the project
dotnet build src/YodaStoriesNG.Engine

# Run (make sure yodesk.dta is in the data folder)
dotnet run --project src/YodaStoriesNG.Engine
```

## Controls

### Keyboard

| Key | Action |
|-----|--------|
| **WASD** / **Arrow Keys** | Move |
| **Space** | Action / Talk / Attack |
| **1-8** | Select inventory item |
| **Tab** | Toggle weapon |
| **O** | Show mission objective |
| **X** | Travel (X-Wing) |
| **R** | Restart game |
| **Escape** | Quit |

### Xbox Controller

| Button | Action |
|--------|--------|
| **Left Stick** / **D-Pad** | Move |
| **A** | Action / Talk / Attack |
| **B** | Cancel / Dismiss dialogue |
| **X** | Travel (X-Wing) |
| **Y** | Show mission objective |
| **LB / RB** | Toggle weapon |
| **Start** | Restart game |
| **Back** | Quit |

## Game Data

This project requires the original `yodesk.dta` file from Star Wars: Yoda Stories.

Place the file in the `data/` folder or run the game from the directory containing your Yoda Stories installation.

## Project Structure

```
YodaStoriesNG/
├── src/
│   └── YodaStoriesNG.Engine/
│       ├── Audio/           # Sound playback
│       ├── Data/            # Game data structures
│       ├── Game/            # Game logic
│       │   ├── GameEngine.cs
│       │   ├── GameState.cs
│       │   ├── WorldGenerator.cs
│       │   ├── ActionExecutor.cs
│       │   └── NPC.cs
│       ├── Parsing/         # DTA file parser
│       ├── Rendering/       # SDL2 renderer
│       └── UI/              # Message system
└── README.md
```

## Features

### Implemented

- DTA file parsing (tiles, zones, characters, puzzles, sounds)
- Zone rendering with 3-layer tile system
- Player movement and collision detection
- NPC spawning and AI (wandering, chasing)
- Combat system (melee attacks, health, damage)
- Inventory management
- Weapon system
- Zone transitions (doors, edge scrolling)
- X-Wing travel between Dagobah and planets
- Action script execution (IACT)
- Message and dialogue system
- Sound effects
- Mission/quest system with puzzle chains
- IZAX entity parsing (NPC item handoff)
- Widescreen UI layout
- Xbox controller support

### In Progress

- Full mission chain validation
- Save/load game state
- All action script opcodes

### Planned

- Map editor tool
- Mobile/portrait mode UI
- Additional puzzle types
- Mod support

## Technical Details

### Data Format

The game parses the proprietary `.dta` format which contains:

- **TILE** - 32x32 pixel tiles with palette indices
- **ZONE** - Map data with 3 tile layers
- **CHAR** - Character definitions and animations
- **PUZ2** - Puzzle definitions for quests
- **IACT** - Action scripts (conditions + instructions)
- **IZAX** - Zone auxiliary data (NPC spawns with items)

### Action Scripts

Zone behavior is driven by IACT scripts containing:

- **Conditions**: ZoneEnter, HasItem, NpcIs, TileAtIs, etc.
- **Instructions**: PlaceTile, AddItem, SpeakNpc, ChangeZone, etc.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## Legal

This is a fan project and is not affiliated with or endorsed by LucasArts, Disney, or any related entities. Star Wars and Yoda Stories are trademarks of Lucasfilm Ltd.

You must own a legal copy of Star Wars: Yoda Stories to use this software.

## Acknowledgments

- [WebDes1gn](https://www.webfun.io/) - Yoda Stories file format documentation
- The SDL2 team for the cross-platform multimedia library
- LucasArts for creating the original game

---

*May the Force be with you!*
