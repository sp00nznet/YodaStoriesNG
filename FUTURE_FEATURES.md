# Future Features - WebFun-Style Development Tools

Based on the WebFun implementation (https://codeberg.org/cyco/WebFun), these features would be valuable additions:

## Save Game Inspector
- View and edit saved game state
- Inspect inventory, position, zone state
- Debug mission progress

## Zone Editor
- Visual zone editing with tile placement
- Edit zone actions/scripts (IACT)
- Configure zone objects (NPCs, items, doors)
- WebFun uses "a lisp-like language" for scripting

## Asset Viewer/Editor
- Tile browser with flags visualization
- Character list with animation preview
- Sound browser
- Puzzle data viewer

## Debug Menu
- Toggle debug overlays (collision, zone info, NPC paths)
- Script debugger for zone actions
- Code coverage for in-game scripts
- Zone teleportation
- Item spawning
- God mode / invincibility

## Implementation Notes
- Could be a separate ImGui overlay or SDL-based UI
- Toggle with a debug key (F12 or similar)
- Save/load debug state between sessions
