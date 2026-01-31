using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.UI;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Executes zone action scripts (IACT).
/// </summary>
public class ActionExecutor
{
    private readonly GameData _gameData;
    private readonly GameState _state;
    private readonly Random _random = new();

    // Action execution context
    private int _lastRandomValue;
    private Zone? _currentZone;
    private ActionTrigger _currentTrigger;

    /// <summary>
    /// When true, dialogue instructions are suppressed.
    /// </summary>
    public bool SuppressDialogue { get; set; }

    // Current interaction context
    public int? PlacedItemId { get; set; }
    public int? DroppedItemId { get; set; }
    public int? InteractingNpcId { get; set; }
    public int BumpX { get; set; }
    public int BumpY { get; set; }

    // Event for displaying dialogue
    public event Action<string, string>? OnDialogue;
    public event Action<string>? OnMessage;
    public event Action<int>? OnPlaySound;

    public ActionExecutor(GameData gameData, GameState state)
    {
        _gameData = gameData;
        _state = state;
    }

    /// <summary>
    /// Executes all applicable actions in the current zone.
    /// </summary>
    public void ExecuteZoneActions(ActionTrigger trigger)
    {
        _currentZone = _state.CurrentZone;
        _currentTrigger = trigger;
        if (_currentZone == null)
            return;

        foreach (var action in _currentZone.Actions)
        {
            if (EvaluateConditions(action.Conditions, trigger))
            {
                ExecuteInstructions(action.Instructions);
            }
        }
    }

    /// <summary>
    /// Returns true if the current trigger is player-initiated (NpcTalk, UseItem).
    /// Dialogue should only show for these triggers, not for ZoneEnter, Walk, Bump, etc.
    /// </summary>
    private bool IsPlayerInitiatedTrigger()
    {
        return _currentTrigger == ActionTrigger.NpcTalk ||
               _currentTrigger == ActionTrigger.UseItem;
    }

    private bool EvaluateConditions(List<Condition> conditions, ActionTrigger trigger)
    {
        foreach (var condition in conditions)
        {
            if (!EvaluateCondition(condition, trigger))
                return false;
        }
        return true;
    }

    private bool EvaluateCondition(Condition condition, ActionTrigger trigger)
    {
        var result = EvaluateConditionInternal(condition, trigger);

        // Debug: Log NpcIs condition evaluations (most relevant for R2D2 interaction)
        if (condition.Opcode == ConditionOpcode.NpcIs)
        {
            var args = condition.Arguments;
            var expectedNpc = args.Count > 0 ? args[0] : -1;
            Console.WriteLine($"  Condition NpcIs: expected={expectedNpc}, actual={InteractingNpcId}, result={result}");
        }

        return result;
    }

    private bool EvaluateConditionInternal(Condition condition, ActionTrigger trigger)
    {
        var args = condition.Arguments;

        switch (condition.Opcode)
        {
            case ConditionOpcode.ZoneNotInitialized:
                // True on first entry to zone
                return trigger == ActionTrigger.ZoneEnter && !_state.Variables.ContainsKey(_state.CurrentZoneId + 1000);

            case ConditionOpcode.ZoneEntered:
                return trigger == ActionTrigger.ZoneEnter;

            case ConditionOpcode.Bump:
                if (trigger != ActionTrigger.Bump || args.Count < 2)
                    return false;
                return _state.PlayerX == args[0] && _state.PlayerY == args[1];

            case ConditionOpcode.Standing:
                if (args.Count < 2)
                    return false;
                return _state.PlayerX == args[0] && _state.PlayerY == args[1];

            case ConditionOpcode.CounterIs:
                if (args.Count < 2)
                    return false;
                return _state.GetCounter(args[0]) == args[1];

            case ConditionOpcode.CounterIsNot:
                if (args.Count < 2)
                    return false;
                return _state.GetCounter(args[0]) != args[1];

            case ConditionOpcode.CounterIsGreaterThan:
                if (args.Count < 2)
                    return false;
                return _state.GetCounter(args[0]) > args[1];

            case ConditionOpcode.CounterIsLessThan:
                if (args.Count < 2)
                    return false;
                return _state.GetCounter(args[0]) < args[1];

            case ConditionOpcode.RandomIs:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue == args[0];

            case ConditionOpcode.RandomIsNot:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue != args[0];

            case ConditionOpcode.RandomIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue > args[0];

            case ConditionOpcode.RandomIsLessThan:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue < args[0];

            case ConditionOpcode.HasItem:
                if (args.Count < 1)
                    return false;
                return _state.HasItem(args[0]);

            case ConditionOpcode.TileAtIs:
                if (args.Count < 4 || _currentZone == null)
                    return false;
                return _currentZone.GetTile(args[0], args[1], args[2]) == args[3];

            case ConditionOpcode.ZoneIsSolved:
                if (args.Count < 1)
                    return false;
                return _state.IsZoneSolved(args[0]);

            case ConditionOpcode.HealthIsLessThan:
                if (args.Count < 1)
                    return false;
                return _state.Health < args[0];

            case ConditionOpcode.HealthIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _state.Health > args[0];

            case ConditionOpcode.GamesWonIs:
                if (args.Count < 1)
                    return false;
                return _state.GamesWon == args[0];

            case ConditionOpcode.PlacedItemIs:
                if (args.Count < 1)
                    return false;
                return PlacedItemId == args[0];

            case ConditionOpcode.NoItemPlaced:
                return !PlacedItemId.HasValue;

            case ConditionOpcode.ItemIsPlaced:
                return PlacedItemId.HasValue;

            case ConditionOpcode.DroppedItemIs:
                if (args.Count < 1)
                    return false;
                return DroppedItemId == args[0];

            case ConditionOpcode.NpcIs:
                if (args.Count < 1)
                    return false;
                return InteractingNpcId == args[0];

            case ConditionOpcode.HasNpc:
                return InteractingNpcId.HasValue;

            case ConditionOpcode.MonsterIsDead:
                // Check if specific monster is dead (by index in zone NPCs)
                if (args.Count < 1)
                    return false;
                var npcIndex = args[0];
                if (npcIndex >= 0 && npcIndex < _state.ZoneNPCs.Count)
                    return _state.ZoneNPCs[npcIndex].Health <= 0;
                return true; // If NPC doesn't exist, consider it "dead"

            case ConditionOpcode.HasNoActiveMonsters:
                // Check if all hostile NPCs in zone are dead
                return _state.ZoneNPCs.All(n => !n.IsHostile || n.Health <= 0);

            case ConditionOpcode.RequiredItemIs:
                // Check if zone's required item matches
                if (args.Count < 1 || _currentZone == null)
                    return false;
                // This typically checks puzzle items - for now just check if we have the item
                return _state.HasItem(args[0]);

            case ConditionOpcode.FindItemIs:
                // Similar to RequiredItemIs
                if (args.Count < 1)
                    return false;
                return _state.HasItem(args[0]);

            case ConditionOpcode.EnterByPlane:
                // Check if entered zone by X-Wing
                return _state.XWingPosition.HasValue;

            case ConditionOpcode.HeroIsAt:
                // Check if hero is at position
                if (args.Count < 2)
                    return false;
                return _state.PlayerX == args[0] && _state.PlayerY == args[1];

            case ConditionOpcode.PlacedItemIsNot:
                // Check if placed item does NOT match
                if (args.Count < 1)
                    return false;
                return PlacedItemId != args[0];

            case ConditionOpcode.EndingIs:
                // Check goal item - for now just check if we have the item
                if (args.Count < 1)
                    return false;
                return _state.HasItem(args[0]);

            // Note: SectorCounterIs (0x19) shares value with NpcIs - handled above
            // Note: SectorCounterIsLessThan (0x1A) shares value with HasNpc - handled above
            // Note: HasGoalItem (0x12) shares value with ItemIsPlaced - handled above

            case ConditionOpcode.SectorCounterIsGreaterThan:
                if (args.Count < 2)
                    return false;
                return _state.GetVariable(args[0] + 3000) > args[1];

            case ConditionOpcode.SectorCounterIsNot:
                if (args.Count < 2)
                    return false;
                return _state.GetVariable(args[0] + 3000) != args[1];

            case ConditionOpcode.DropsQuestItemAt:
                // Check if quest item dropped at position
                if (args.Count < 2)
                    return false;
                return BumpX == args[0] && BumpY == args[1] && DroppedItemId.HasValue;

            case ConditionOpcode.HasAnyRequiredItem:
                // Check if has any required item for zone puzzles
                // For now, assume true if inventory is not empty
                return _state.Inventory.Count > 0;

            case ConditionOpcode.GamesWonIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _state.GamesWon > args[0];

            case ConditionOpcode.IsVariable:
                // Same as TileAtIs internally
                if (args.Count < 4 || _currentZone == null)
                    return false;
                return _currentZone.GetTile(args[0], args[1], args[2]) == args[3];

            default:
                // Unknown condition - assume true to allow script to continue
                return true;
        }
    }

    private void ExecuteInstructions(List<Instruction> instructions)
    {
        if (instructions.Count > 0)
        {
            Console.WriteLine($"  Executing {instructions.Count} instructions");
        }

        foreach (var instruction in instructions)
        {
            ExecuteInstruction(instruction);
        }
    }

    private void ExecuteInstruction(Instruction instruction)
    {
        var args = instruction.Arguments;

        switch (instruction.Opcode)
        {
            case InstructionOpcode.PlaceTile:
                if (args.Count >= 4 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], (ushort)args[3]);
                break;

            case InstructionOpcode.RemoveTile:
                if (args.Count >= 3 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], 0xFFFF);
                break;

            case InstructionOpcode.RollDice:
                if (args.Count >= 1)
                    _lastRandomValue = _random.Next(args[0]);
                break;

            case InstructionOpcode.SetCounter:
                if (args.Count >= 2)
                    _state.SetCounter(args[0], args[1]);
                break;

            case InstructionOpcode.AddToCounter:
                if (args.Count >= 2)
                    _state.AddToCounter(args[0], args[1]);
                break;

            case InstructionOpcode.SetVariable:
                if (args.Count >= 2)
                    _state.SetVariable(args[0], args[1]);
                break;

            case InstructionOpcode.AddItem:
                if (args.Count >= 1)
                    _state.AddItem(args[0]);
                break;

            case InstructionOpcode.RemoveItem:
                if (args.Count >= 1)
                    _state.RemoveItem(args[0]);
                break;

            case InstructionOpcode.MarkAsSolved:
                _state.MarkZoneSolved(_state.CurrentZoneId);
                break;

            case InstructionOpcode.WinGame:
                _state.IsGameWon = true;
                _state.GamesWon++;
                break;

            case InstructionOpcode.LoseGame:
                _state.IsGameOver = true;
                break;

            case InstructionOpcode.ChangeZone:
                if (args.Count >= 3)
                {
                    _state.CurrentZoneId = args[0];
                    _state.PlayerX = args[1];
                    _state.PlayerY = args[2];
                }
                break;

            case InstructionOpcode.MoveHeroTo:
                if (args.Count >= 2)
                {
                    _state.PlayerX = args[0];
                    _state.PlayerY = args[1];
                }
                break;

            case InstructionOpcode.MoveHeroBy:
                if (args.Count >= 2)
                {
                    _state.PlayerX += args[0];
                    _state.PlayerY += args[1];
                }
                break;

            case InstructionOpcode.AddHealth:
                if (args.Count >= 1)
                    _state.Health = Math.Min(_state.Health + args[0], _state.MaxHealth);
                break;

            case InstructionOpcode.SubtractHealth:
                if (args.Count >= 1)
                    _state.Health = Math.Max(_state.Health - args[0], 0);
                break;

            case InstructionOpcode.SetHealth:
                if (args.Count >= 1)
                    _state.Health = Math.Clamp(args[0], 0, _state.MaxHealth);
                break;

            case InstructionOpcode.SpeakHero:
                // Only show dialogue if not suppressed AND trigger is player-initiated
                if (!string.IsNullOrEmpty(instruction.Text) && !SuppressDialogue && IsPlayerInitiatedTrigger())
                {
                    OnDialogue?.Invoke("Luke", instruction.Text);
                }
                break;

            case InstructionOpcode.SpeakNpc:
            case InstructionOpcode.SpeakNpc2:
                // Only show dialogue if not suppressed AND trigger is player-initiated
                if (!string.IsNullOrEmpty(instruction.Text) && !SuppressDialogue && IsPlayerInitiatedTrigger())
                {
                    // Get NPC name from argument if provided
                    string npcName = "NPC";
                    if (args.Count >= 1 && args[0] >= 0 && args[0] < _gameData.Characters.Count)
                    {
                        var character = _gameData.Characters[args[0]];
                        if (!string.IsNullOrEmpty(character.Name))
                            npcName = character.Name;
                    }
                    OnDialogue?.Invoke(npcName, instruction.Text);
                }
                break;

            case InstructionOpcode.PlaySound:
                if (args.Count >= 1)
                {
                    OnPlaySound?.Invoke(args[0]);
                }
                break;

            case InstructionOpcode.Wait:
                // TODO: Implement wait/delay
                break;

            case InstructionOpcode.DropItem:
                // Drop an item at specified location
                if (args.Count >= 3 && _currentZone != null)
                {
                    int x = args[0];
                    int y = args[1];
                    int tileId = args[2];
                    // Place the item tile on layer 1 (object layer)
                    _currentZone.SetTile(x, y, 1, (ushort)tileId);
                    Console.WriteLine($"Dropped item {tileId} at ({x},{y})");
                }
                break;

            case InstructionOpcode.EnableMonster:
                if (args.Count >= 1)
                {
                    int npcIdx = args[0];
                    if (npcIdx >= 0 && npcIdx < _state.ZoneNPCs.Count)
                        _state.ZoneNPCs[npcIdx].IsEnabled = true;
                }
                break;

            case InstructionOpcode.DisableMonster:
                if (args.Count >= 1)
                {
                    int npcIdx = args[0];
                    if (npcIdx >= 0 && npcIdx < _state.ZoneNPCs.Count)
                        _state.ZoneNPCs[npcIdx].IsEnabled = false;
                }
                break;

            case InstructionOpcode.EnableAllMonsters:
                foreach (var npc in _state.ZoneNPCs)
                    npc.IsEnabled = true;
                break;

            case InstructionOpcode.DisableAllMonsters:
                foreach (var npc in _state.ZoneNPCs)
                    npc.IsEnabled = false;
                break;

            case InstructionOpcode.HideHero:
                // TODO: Make hero invisible
                break;

            case InstructionOpcode.ShowHero:
                // TODO: Make hero visible
                break;

            case InstructionOpcode.SetZoneType:
                // Change the zone's type
                if (args.Count >= 1 && _currentZone != null)
                    _currentZone.Type = (ZoneType)args[0];
                break;

            case InstructionOpcode.DisableAction:
                // This typically disables the current action from running again
                // Mark with a variable
                _state.SetVariable(_state.CurrentZoneId + 2000, 1);
                break;

            case InstructionOpcode.Redraw:
                // Request a redraw - handled by renderer
                break;

            case InstructionOpcode.MoveTile:
                // Move a tile from one position to another
                if (args.Count >= 5 && _currentZone != null)
                {
                    int fromX = args[0];
                    int fromY = args[1];
                    int layer = args[2];
                    int toX = args[3];
                    int toY = args[4];
                    var tile = _currentZone.GetTile(fromX, fromY, layer);
                    _currentZone.SetTile(fromX, fromY, layer, 0xFFFF);
                    _currentZone.SetTile(toX, toY, layer, tile);
                }
                break;

            case InstructionOpcode.DrawTile:
                // Draw a tile at position (same as PlaceTile for us)
                if (args.Count >= 4 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], (ushort)args[3]);
                break;

            case InstructionOpcode.SetTileNeedsDisplay:
                // Redraw tile at position - handled by renderer automatically
                break;

            case InstructionOpcode.SetRectNeedsDisplay:
                // Redraw rectangle area - handled by renderer automatically
                break;

            case InstructionOpcode.StopSound:
                // Stop currently playing sounds
                // TODO: Implement sound stopping
                break;

            case InstructionOpcode.EnableHotspot:
                // Enable a hotspot (zone object) at specified index
                if (args.Count >= 1 && _currentZone != null)
                {
                    int idx = args[0];
                    if (idx >= 0 && idx < _currentZone.Objects.Count)
                    {
                        // Hotspots are typically zone objects - enable by restoring their tile
                        var obj = _currentZone.Objects[idx];
                        if (obj.Argument > 0)
                            _currentZone.SetTile(obj.X, obj.Y, 1, (ushort)obj.Argument);
                    }
                }
                break;

            case InstructionOpcode.DisableHotspot:
                // Disable a hotspot (zone object) at specified index
                if (args.Count >= 1 && _currentZone != null)
                {
                    int idx = args[0];
                    if (idx >= 0 && idx < _currentZone.Objects.Count)
                    {
                        var obj = _currentZone.Objects[idx];
                        // Remove the tile
                        _currentZone.SetTile(obj.X, obj.Y, 1, 0xFFFF);
                    }
                }
                break;

            // Note: SetSectorCounter (0x22) shares value with SetZoneType - handled above

            case InstructionOpcode.AddToSectorCounter:
                // Add to zone sector counter
                if (args.Count >= 2)
                {
                    int current = _state.GetVariable(args[0] + 3000);
                    _state.SetVariable(args[0] + 3000, current + args[1]);
                }
                break;

            case InstructionOpcode.SetRandom:
                // Set zone random to specific value
                if (args.Count >= 1)
                    _lastRandomValue = args[0];
                break;

            default:
                // Unknown instruction - log and continue
                Console.WriteLine($"Unknown instruction: {instruction.Opcode}");
                break;
        }
    }
}

public enum ActionTrigger
{
    ZoneEnter,
    ZoneLeave,
    Bump,
    Walk,
    UseItem,
    Attack,
    NpcTalk,
}
