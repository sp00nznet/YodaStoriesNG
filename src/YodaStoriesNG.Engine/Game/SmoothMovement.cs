using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    private bool _smoothMovement;
    private bool _smoothMovementRequested;
    private bool _committingSmoothStep;
    private bool _movementFocused = true;
    private bool _discardMovementTime;
    private bool _suppressHeldMovement;
    private MovementStep? _playerStep;
    private readonly Dictionary<NPC, MovementStep> _npcSteps = new();
    private System.Action? _pendingMovementAction;
    private KeyBindings _movementBindings = new();
    private double _inputMoveStrength = 1;
    private double _bumpCooldown;
    private bool _smoothInputVertical;
    private Func<(int X, int Y, double Strength)>? _smoothInputSource;
    private Func<bool>? _smoothPullSource;
    private bool ReadPullInput() => _smoothPullSource?.Invoke() ?? (_renderer != null && IsShiftHeld());
    private bool MovementModePending => _smoothMovement != _smoothMovementRequested;
    private bool MovementUIBlocked => !_movementFocused || _debugMapWindow?.IsLocatorOpen == true || _menuBar?.IsMenuOpen == true ||
        _controllerOverlay?.IsOpen == true || _controlsWindow?.IsOpen == true || _state.IsPaused;
    private (double X, double Y) PlayerRenderPosition => _playerStep is { } step
        ? (step.X, step.Y) : (_state.PlayerX, _state.PlayerY);
    private (double X, double Y) NPCRenderPosition(NPC npc) => _npcSteps.TryGetValue(npc, out var step)
        ? (step.X, step.Y) : (npc.X, npc.Y);
    private double ViewCameraX => _smoothMovement && _state.CurrentZone is { } zone
        ? Math.Clamp(PlayerRenderPosition.X - GameRenderer.ViewportTilesX / 2, 0, Math.Max(0, zone.Width - GameRenderer.ViewportTilesX))
        : _state.CameraX;
    private double ViewCameraY => _smoothMovement && _state.CurrentZone is { } zone
        ? Math.Clamp(PlayerRenderPosition.Y - GameRenderer.ViewportTilesY / 2, 0, Math.Max(0, zone.Height - GameRenderer.ViewportTilesY))
        : _state.CameraY;

    private void SetSmoothMovement(bool enabled)
    {
        _smoothMovementRequested = enabled;
        ApplyPendingMovementMode();
        if (_menuBar != null) _menuBar.SmoothMovement = enabled;
        var settings = KeyBindings.Load();
        settings.SmoothMovement = enabled;
        settings.Save();
        // The controls window keeps its own copy; preserve this setting when rebinding.
        if (_controlsWindow != null) _controlsWindow.GetKeyBindings().SmoothMovement = enabled;
    }

    private void ApplyPendingMovementMode()
    {
        if (_playerStep != null || _npcSteps.Count != 0) return;
        _smoothMovement = _smoothMovementRequested;
    }

    private void CancelSmoothMovement()
    {
        ClearItemDrag();
        _playerStep = null;
        _npcSteps.Clear();
        _pendingMovementAction = null;
        _bumpCooldown = 0;
        _suppressHeldMovement = true;
        ResetPlayerAnimation();
        ApplyPendingMovementMode();
    }

    private static Direction StepFacing(int dx, int dy, Direction previous)
    {
        if (dx != 0 && dy != 0 && ((dx > 0 && previous == Direction.Right) ||
            (dx < 0 && previous == Direction.Left) || (dy > 0 && previous == Direction.Down) ||
            (dy < 0 && previous == Direction.Up))) return previous;
        return dx != 0 ? (dx > 0 ? Direction.Right : Direction.Left) : (dy > 0 ? Direction.Down : Direction.Up);
    }

    private bool MovementReserved(int x, int y, NPC? except = null)
    {
        if (_playerStep?.Occupies(x, y) == true) return true;
        return _npcSteps.Any(pair => pair.Key != except && pair.Value.Occupies(x, y));
    }

    private bool HasStepConflict(MovementStep step, NPC? except = null)
    {
        if (except != null && _playerStep?.Conflicts(step) == true) return true;
        return _npcSteps.Any(pair => pair.Key != except && pair.Value.Conflicts(step));
    }

    private bool SmoothCellClear(int x, int y, NPC? actor = null, bool collect = false)
    {
        var zone = _state.CurrentZone;
        if (zone == null || x < 0 || y < 0 || x >= zone.Width || y >= zone.Height) return false;
        if (IsGroundBlocked(zone, x, y) || IsScriptedActorTile(zone, x, y)) return false;
        if (actor != null && x == _state.PlayerX && y == _state.PlayerY) return false;
        if (MovementReserved(x, y, actor)) return false;
        if (_state.ZoneNPCs.Any(n => n != actor && n.IsEnabled && n.IsAlive && n.X == x && n.Y == y &&
            !(collect && n.IsReadyToCollect && IsR2D2Character(n.CharacterId)))) return false;
        int tile = zone.GetTile(x, y, 1);
        return tile == ushort.MaxValue || tile >= _gameData!.Tiles.Count ||
            !_gameData.Tiles[tile].IsSolid || IsNPCMapSprite(x, y, (ushort)tile);
    }

    private bool DiagonalSideClear(int x, int y)
    {
        var zone = _state.CurrentZone;
        if (zone == null || x < 0 || y < 0 || x >= zone.Width || y >= zone.Height ||
            IsGroundBlocked(zone, x, y) || IsScriptedActorTile(zone, x, y) || MovementReserved(x, y)) return false;
        if (_state.ZoneNPCs.Any(n => n.IsEnabled && n.IsAlive && n.X == x && n.Y == y)) return false;
        int tile = zone.GetTile(x, y, 1);
        // Authored maps contain diagonal openings beside walls and movable rocks.
        // Side cells are not entered: the destination still needs to be walkable,
        // and pushing or pulling a block remains cardinal.
        return tile == ushort.MaxValue || tile < _gameData!.Tiles.Count &&
            !_gameData.Tiles[tile].IsCharacter;
    }

    private bool CanSlidePlayerTo(int dx, int dy)
    {
        int x = _state.PlayerX, y = _state.PlayerY;
        return DiagonalSideClear(x + dx, y) && DiagonalSideClear(x, y + dy) &&
            SmoothCellClear(x + dx, y + dy, collect: true) && !IsCardinalInteraction(x + dx, y + dy) &&
            !_state.CurrentZone!.Objects.Any(o => o.Enabled && o.X == x + dx && o.Y == y + dy &&
                o.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter or
                    ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary);
    }

    private bool IsCardinalInteraction(int x, int y) => IsScriptedActorTile(_state.CurrentZone!, x, y) ||
        _sectorPuzzles?.IsBumpTarget(x, y) == true || _state.CurrentZone!.Objects.Any(o => o.Enabled && o.X == x && o.Y == y &&
        o.Type is ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah) ||
        _state.CurrentZone.GetTile(x, y, 1) is var tile && tile < _gameData!.Tiles.Count && _gameData.Tiles[tile].IsDraggable;

    private bool RequestSmoothPlayerStep(int dx, int dy, Direction facing, bool pull)
    {
        if (_playerStep != null || MovementModePending || MovementUIBlocked || _state.IsGameOver ||
            _actionExecutor?.IsBusy == true || !_state.HeroVisible || _messages.HasDialogue || _state.CurrentZone == null) return false;
        dx = Math.Sign(dx); dy = Math.Sign(dy);
        if (dx == 0 && dy == 0) return false;
        int x = _state.PlayerX, y = _state.PlayerY;
        bool diagonal = dx != 0 && dy != 0;
        if (diagonal && (pull || !DiagonalSideClear(x + dx, y) || !DiagonalSideClear(x, y + dy) ||
            !SmoothCellClear(x + dx, y + dy, collect: true) || IsCardinalInteraction(x + dx, y + dy) ||
            _state.CurrentZone.Objects.Any(o => o.Enabled && o.X == x + dx && o.Y == y + dy &&
                o.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter or
                    ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary))) return false;
        facing = StepFacing(dx, dy, facing);
        _state.PlayerDirection = facing;
        bool clear = SmoothCellClear(x + dx, y + dy, collect: true);
        // Pushes are validated now and applied by the original movement path at arrival.
        int tile = _state.CurrentZone.GetTile(Math.Clamp(x + dx, 0, _state.CurrentZone.Width - 1),
            Math.Clamp(y + dy, 0, _state.CurrentZone.Height - 1), 1);
        bool push = !diagonal && x + dx >= 0 && y + dy >= 0 &&
            x + dx < _state.CurrentZone.Width && y + dy < _state.CurrentZone.Height &&
            !IsGroundBlocked(_state.CurrentZone, x + dx, y + dy) &&
            tile < _gameData!.Tiles.Count && _gameData.Tiles[tile].IsDraggable &&
            _state.CurrentZone.GetTile(x + dx * 2, y + dy * 2, 1) == ushort.MaxValue &&
            SmoothCellClear(x + dx * 2, y + dy * 2) && !MovementReserved(x + dx, y + dy);
        if (!clear && !push || !diagonal && IsCardinalInteraction(x + dx, y + dy) && !push)
        {
            // Bumps, boarding and exits use the original cardinal interaction path.
            if (!diagonal && _bumpCooldown <= 0 && !MovementReserved(x + dx, y + dy))
            {
                int previousZone = _state.CurrentZoneId;
                _bumpCooldown = 1.0 / 6;
                _committingSmoothStep = true;
                try { TryMovePlayer(dx, dy, facing, pull); }
                finally { _committingSmoothStep = false; }
                if (_messages.HasDialogue || _actionExecutor?.IsBusy == true || !_state.HeroVisible ||
                    previousZone != _state.CurrentZoneId) return true;
            }
            if (!diagonal && !pull && !IsCardinalInteraction(x + dx, y + dy))
                foreach (var slide in dx == 0 ? new[] { (-1, dy), (1, dy) } : new[] { (dx, 1), (dx, -1) })
                    if (CanSlidePlayerTo(slide.Item1, slide.Item2) &&
                        RequestSmoothPlayerStep(slide.Item1, slide.Item2, facing, false)) return true;
            return false;
        }
        var step = new MovementStep(x, y, dx, dy, 1.0 / (6 * Math.Max(0.1, _inputMoveStrength)), facing, pull)
        { BlockDestination = push ? (x + dx * 2, y + dy * 2) : null };
        if (HasStepConflict(step)) return false;
        // A push also owns the block's destination until arrival.
        if (push && _npcSteps.Values.Any(s => s.Occupies(x + dx * 2, y + dy * 2))) return false;
        _playerStep = step;
        return true;
    }

    private bool IsSmoothMovementKey(int key)
    {
        var bindings = _controlsWindow?.GetKeyBindings() ?? _movementBindings;
        return new[] { KeyBindings.MoveUp, KeyBindings.MoveDown, KeyBindings.MoveLeft, KeyBindings.MoveRight }
            .Any(action => bindings.IsPressed(action, key));
    }

    private (int X, int Y, double Strength) ReadSmoothInput()
    {
        _smoothInputVertical = false;
        if (_smoothInputSource != null) return _smoothInputSource();
        if (_renderer == null) return (0, 0, 1); // Headless audits inject steps directly.
        var bindings = _controlsWindow?.GetKeyBindings() ?? _movementBindings;
        int count;
        var keys = SDL.GetKeyboardState(&count);
        int keyCount = count;
        bool Held(string action)
        {
            bool Down(Dictionary<string, int> map)
            {
                if (!map.TryGetValue(action, out int key) || key == 0) return false;
                int scan = (int)SDL.GetScancodeFromKey(key);
                return scan >= 0 && scan < keyCount && keys[scan] != 0;
            }
            return Down(bindings.Primary) || Down(bindings.Alternate);
        }
        int x = (Held(KeyBindings.MoveRight) ? 1 : 0) - (Held(KeyBindings.MoveLeft) ? 1 : 0);
        int y = (Held(KeyBindings.MoveDown) ? 1 : 0) - (Held(KeyBindings.MoveUp) ? 1 : 0);
        if (x != 0 || y != 0) return (x, y, 1);
        if (_controllerConnected && _controller != null)
        {
            x = (SDL.GameControllerGetButton(_controller, SDLGameControllerButton.DpadRight) != 0 ? 1 : 0) -
                (SDL.GameControllerGetButton(_controller, SDLGameControllerButton.DpadLeft) != 0 ? 1 : 0);
            y = (SDL.GameControllerGetButton(_controller, SDLGameControllerButton.DpadDown) != 0 ? 1 : 0) -
                (SDL.GameControllerGetButton(_controller, SDLGameControllerButton.DpadUp) != 0 ? 1 : 0);
            if (x != 0 || y != 0) return (x, y, 1);
            double ax = SDL.GameControllerGetAxis(_controller, SDLGameControllerAxis.Leftx);
            double ay = SDL.GameControllerGetAxis(_controller, SDLGameControllerAxis.Lefty);
            double magnitude = Math.Sqrt(ax * ax + ay * ay);
            if (magnitude > ControllerDeadzone)
            {
                _smoothInputVertical = Math.Abs(ay) > Math.Abs(ax);
                return (Math.Abs(ax) > ControllerDeadzone ? Math.Sign(ax) : 0,
                    Math.Abs(ay) > ControllerDeadzone ? Math.Sign(ay) : 0,
                    Math.Clamp((magnitude - ControllerDeadzone) / (32767 - ControllerDeadzone), 0.1, 1));
            }
        }
        return (0, 0, 1);
    }

    private void PollSmoothMovement()
    {
        var input = ReadSmoothInput();
        if (input.X == 0 && input.Y == 0) _suppressHeldMovement = false;
        if (MovementUIBlocked || _actionExecutor?.IsBusy == true || !_state.HeroVisible || _messages.HasDialogue)
        { _suppressHeldMovement = true; return; }
        if (input.X == 0 && input.Y == 0) return;
        if (_suppressHeldMovement || _bot?.IsRunning == true || _playerStep != null || MovementModePending) return;
        _inputMoveStrength = input.Strength;
        int dx = input.X, dy = input.Y;
        if (!RequestSmoothPlayerStep(dx, dy, _state.PlayerDirection, ReadPullInput()) && dx != 0 && dy != 0)
        {
            // Digital ties choose horizontal. Analog input uses its dominant axis.
            bool vertical = _smoothInputVertical;
            if (vertical)
            {
                if (!RequestSmoothPlayerStep(0, dy, _state.PlayerDirection, ReadPullInput()))
                    RequestSmoothPlayerStep(dx, 0, _state.PlayerDirection, ReadPullInput());
            }
            else if (!RequestSmoothPlayerStep(dx, 0, _state.PlayerDirection, ReadPullInput()))
                RequestSmoothPlayerStep(0, dy, _state.PlayerDirection, ReadPullInput());
        }
        _inputMoveStrength = 1;
    }

    private void UpdateSmoothMovement(double deltaTime)
    {
        if (_state.IsGameOver || _state.Health <= 0) { CancelSmoothMovement(); return; }
        // Scripts can relocate a hero directly. Never tween across a scripted jump.
        if (_playerStep is { } stale && (stale.FromX != _state.PlayerX || stale.FromY != _state.PlayerY ||
            _actionExecutor?.IsBusy == true || !_state.HeroVisible)) CancelSmoothMovement();
        if (_actionExecutor?.IsBusy == true || !_state.HeroVisible)
        { CancelSmoothMovement(); return; }
        if (MovementUIBlocked) { _suppressHeldMovement = true; return; }
        if (!_smoothMovement) { ApplyPendingMovementMode(); return; }
        _bumpCooldown = Math.Max(0, _bumpCooldown - deltaTime);
        PollSmoothMovement();
        double remaining = Math.Max(0, deltaTime);
        for (int i = 0; i < 64 && _playerStep is { } step; i++)
        {
            if (!step.Advance(remaining)) break;
            remaining = Math.Max(0, step.Elapsed - step.Duration);
            _playerStep = null;
            _committingSmoothStep = true;
            // A modifier pressed during a smooth step should still pull the block
            // adjacent to its starting tile. Diagonal steps never pull diagonally.
            bool pull = step.Pull || (step.FromX == step.ToX || step.FromY == step.ToY) && ReadPullInput();
            try { TryMovePlayer(step.ToX - step.FromX, step.ToY - step.FromY, step.Facing, pull); }
            finally { _committingSmoothStep = false; }
            if (_actionExecutor?.IsBusy == true || !_state.HeroVisible) break;
            if (_pendingMovementAction is { } action)
            { _pendingMovementAction = null; action(); break; }
            if (remaining < 1e-10) break;
            PollSmoothMovement();
        }
        if (_actionExecutor?.IsBusy == true || !_state.HeroVisible)
        { CancelSmoothMovement(); return; }
        foreach (var pair in _npcSteps.ToArray())
        {
            var npc = pair.Key; var step = pair.Value;
            if (!npc.IsAlive || !npc.IsEnabled || npc.X != step.FromX || npc.Y != step.FromY)
            { _npcSteps.Remove(npc); continue; }
            if (npc.StunTimer > 0) continue;
            if (!step.Advance(deltaTime)) continue;
            npc.X = step.ToX; npc.Y = step.ToY;
            _npcSteps.Remove(npc);
            npc.AnimationFrame = 0;
            if (npc.Behavior == NPCBehavior.Patrol && npc.Waypoints.Length > 0 &&
                npc.Waypoints[npc.CurrentWaypoint] == (npc.X, npc.Y))
                npc.CurrentWaypoint = (npc.CurrentWaypoint + 1) % npc.Waypoints.Length;
        }
        if (_playerStep != null)
        {
            _playerWalkingTimer = AnimationFrameTime * 3;
        }
        else if (_smoothMovement) ResetPlayerAnimation();
        ApplyPendingMovementMode();
    }

    private void StartSmoothNPCStep(NPC npc)
    {
        if (MovementModePending || _npcSteps.ContainsKey(npc) || _actionExecutor?.IsBusy == true ||
            _state.IsGameOver || MovementUIBlocked) return;
        int dx, dy;
        switch (npc.Behavior)
        {
            case NPCBehavior.Chasing:
            case NPCBehavior.Fleeing:
                dx = _state.PlayerX - npc.X; dy = _state.PlayerY - npc.Y;
                if (npc.Behavior == NPCBehavior.Fleeing) { dx = -dx; dy = -dy; }
                break;
            case NPCBehavior.Patrol:
                if (npc.Waypoints.Length == 0) goto case NPCBehavior.Wandering;
                var target = npc.Waypoints[npc.CurrentWaypoint];
                if (target == (0, 0) || target == (npc.X, npc.Y))
                { npc.CurrentWaypoint = (npc.CurrentWaypoint + 1) % npc.Waypoints.Length; return; }
                dx = target.X - npc.X; dy = target.Y - npc.Y;
                break;
            case NPCBehavior.Wandering:
                var directions = new (int X, int Y)[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
                (dx, dy) = directions[_random.Next(directions.Length)];
                break;
            default:
                if (npc.Behavior == NPCBehavior.Stationary && npc.DistanceTo(_state.PlayerX, _state.PlayerY) <= 3)
                    npc.Direction = GetDirectionToward(npc.X, npc.Y, _state.PlayerX, _state.PlayerY);
                return;
        }
        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        if (TryStart(sx, sy)) return;
        if (horizontal) { if (!TryStart(sx, 0)) TryStart(0, sy); }
        else if (!TryStart(0, sy)) TryStart(sx, 0);

        bool TryStart(int x, int y)
        {
            if (x == 0 && y == 0) return false;
            int tx = npc.X + x, ty = npc.Y + y;
            if (npc.Behavior == NPCBehavior.Wandering &&
                (Math.Abs(tx - npc.StartX) > npc.WanderRadius || Math.Abs(ty - npc.StartY) > npc.WanderRadius)) return false;
            if (!SmoothCellClear(tx, ty, npc) || x != 0 && y != 0 &&
                (!SmoothCellClear(tx, npc.Y, npc) || !SmoothCellClear(npc.X, ty, npc))) return false;
            var step = new MovementStep(npc.X, npc.Y, x, y, npc.MoveCooldown, StepFacing(x, y, npc.Direction));
            if (HasStepConflict(step, npc)) return false;
            npc.Direction = step.Facing;
            npc.AnimationFrame = 0;
            _npcSteps[npc] = step;
            return true;
        }
    }

    private static bool ProjectileTouches(Projectile projectile, (double X, double Y) actor) =>
        projectile.X >= actor.X && projectile.X < actor.X + 1 &&
        projectile.Y >= actor.Y && projectile.Y < actor.Y + 1;
}
