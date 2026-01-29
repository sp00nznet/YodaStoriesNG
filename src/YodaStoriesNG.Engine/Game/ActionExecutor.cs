using YodaStoriesNG.Engine.Data;

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

            default:
                // Unknown condition - assume true to allow script to continue
                return true;
        }
    }

    private void ExecuteInstructions(List<Instruction> instructions)
    {
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
            case InstructionOpcode.SpeakNpc:
            case InstructionOpcode.SpeakNpc2:
                // TODO: Display dialog text
                if (!string.IsNullOrEmpty(instruction.Text))
                    Console.WriteLine($"Dialog: {instruction.Text}");
                break;

            case InstructionOpcode.PlaySound:
                // TODO: Play sound effect
                if (args.Count >= 1)
                    Console.WriteLine($"Play sound: {args[0]}");
                break;

            case InstructionOpcode.Wait:
                // TODO: Implement wait/delay
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
}
