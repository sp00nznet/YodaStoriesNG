using IndyNG.Engine.Data;

namespace IndyNG.Engine.Game;

/// <summary>
/// Core game engine for Indiana Jones Desktop Adventures
/// </summary>
public class GameEngine
{
    private readonly GameData _gameData;

    // Current zone
    public int CurrentZoneId { get; private set; }
    public Zone? CurrentZone { get; private set; }

    // Player state
    public int PlayerX { get; private set; }
    public int PlayerY { get; private set; }
    public Direction PlayerDirection { get; private set; } = Direction.Down;

    // Inventory
    public List<int> Inventory { get; } = new();
    public int? SelectedItem { get; set; }

    // NPCs in current zone
    public List<NPC> ZoneNPCs { get; } = new();

    public GameEngine(GameData gameData)
    {
        _gameData = gameData;
    }

    public void LoadZone(int zoneId)
    {
        if (zoneId < 0 || zoneId >= _gameData.Zones.Count)
        {
            Console.WriteLine($"Invalid zone ID: {zoneId}");
            return;
        }

        var zone = _gameData.Zones[zoneId];
        if (zone.Width == 0 || zone.Height == 0)
        {
            Console.WriteLine($"Zone {zoneId} is empty");
            return;
        }

        CurrentZoneId = zoneId;
        CurrentZone = zone;

        // Find spawn location
        var spawn = zone.Objects.FirstOrDefault(o => o.Type == ZoneObjectType.SpawnLocation);
        if (spawn != null)
        {
            PlayerX = spawn.X;
            PlayerY = spawn.Y;
        }
        else
        {
            // Default to center
            PlayerX = zone.Width / 2;
            PlayerY = zone.Height / 2;
        }

        // Ensure spawn position is walkable
        EnsureWalkablePosition();

        // Load NPCs from IZAX data
        LoadZoneNPCs();

        Console.WriteLine($"Loaded zone {zoneId}: {zone.Width}x{zone.Height}, Type={zone.Type}, Planet={zone.Planet}");
        Console.WriteLine($"  Player at ({PlayerX}, {PlayerY}), {ZoneNPCs.Count} NPCs");

        // Debug: show some tile values
        if (zone.Width > 0 && zone.Height > 0)
        {
            var mid = zone.GetTile(zone.Width / 2, zone.Height / 2, 1);
            var floor = zone.GetTile(zone.Width / 2, zone.Height / 2, 0);
            Console.WriteLine($"  Center tile: floor={floor}, middle={mid} (0xFFFF={0xFFFF})");
        }
    }

    private void EnsureWalkablePosition()
    {
        if (CurrentZone == null) return;

        // Check if current position is walkable
        if (IsWalkable(PlayerX, PlayerY)) return;

        Console.WriteLine($"  Spawn position ({PlayerX}, {PlayerY}) is blocked, searching for walkable position...");

        // Search from center, staying away from edges (leave 1 tile margin)
        int centerX = CurrentZone.Width / 2;
        int centerY = CurrentZone.Height / 2;
        int margin = 1;

        // First pass: find any tile with empty middle layer, preferring center
        for (int radius = 0; radius < Math.Max(CurrentZone.Width, CurrentZone.Height); radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                    int nx = centerX + dx;
                    int ny = centerY + dy;

                    // Stay away from edges
                    if (nx < margin || nx >= CurrentZone.Width - margin ||
                        ny < margin || ny >= CurrentZone.Height - margin)
                        continue;

                    // Check if this position is walkable
                    if (IsWalkable(nx, ny))
                    {
                        PlayerX = nx;
                        PlayerY = ny;
                        Console.WriteLine($"  Found walkable position at ({PlayerX}, {PlayerY})");
                        return;
                    }
                }
            }
        }

        // Second pass: try anywhere including edges
        for (int y = margin; y < CurrentZone.Height - margin; y++)
        {
            for (int x = margin; x < CurrentZone.Width - margin; x++)
            {
                if (IsWalkable(x, y))
                {
                    PlayerX = x;
                    PlayerY = y;
                    Console.WriteLine($"  Found walkable position at ({PlayerX}, {PlayerY}) (second pass)");
                    return;
                }
            }
        }

        // Fallback: use center
        PlayerX = centerX;
        PlayerY = centerY;
        Console.WriteLine($"  No walkable position found, using center ({PlayerX}, {PlayerY})");
    }

    private void LoadZoneNPCs()
    {
        ZoneNPCs.Clear();

        if (CurrentZone?.AuxData?.Entities == null) return;

        foreach (var entity in CurrentZone.AuxData.Entities)
        {
            if (entity.CharacterId < 0 || entity.CharacterId >= _gameData.Characters.Count)
                continue;

            var character = _gameData.Characters[entity.CharacterId];
            var npc = new NPC
            {
                CharacterId = entity.CharacterId,
                X = entity.X,
                Y = entity.Y,
                IsEnabled = true,
                IsAlive = true,
                Name = character.Name,
                // Determine if hostile based on name
                IsHostile = IsLikelyHostile(character.Name)
            };

            if (entity.ItemTileId > 0 && entity.ItemTileId != 0xFFFF)
            {
                npc.CarriedItemId = entity.ItemTileId;
            }

            ZoneNPCs.Add(npc);
        }
    }

    private bool IsLikelyHostile(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        return lower.Contains("guard") || lower.Contains("nazi") ||
               lower.Contains("soldier") || lower.Contains("enemy") ||
               lower.Contains("snake") || lower.Contains("spider") ||
               lower.Contains("thug") || lower.Contains("tribal");
    }

    public void MovePlayer(int dx, int dy)
    {
        if (CurrentZone == null) return;

        // Set direction
        if (dx < 0) PlayerDirection = Direction.Left;
        else if (dx > 0) PlayerDirection = Direction.Right;
        else if (dy < 0) PlayerDirection = Direction.Up;
        else if (dy > 0) PlayerDirection = Direction.Down;

        int newX = PlayerX + dx;
        int newY = PlayerY + dy;

        // Check bounds
        if (newX < 0 || newX >= CurrentZone.Width || newY < 0 || newY >= CurrentZone.Height)
        {
            // Try zone transition
            TryZoneTransition(dx, dy);
            return;
        }

        // Check collision
        if (!IsWalkable(newX, newY))
        {
            // Try to push draggable objects
            TryPush(newX, newY, dx, dy);
            return;
        }

        // Check NPC collision
        if (ZoneNPCs.Any(n => n.IsEnabled && n.IsAlive && n.X == newX && n.Y == newY))
        {
            return;
        }

        // Move
        PlayerX = newX;
        PlayerY = newY;

        // Check for pickups/triggers at new position
        CheckPositionTriggers();
    }

    private bool IsWalkable(int x, int y)
    {
        if (CurrentZone == null) return false;
        if (x < 0 || x >= CurrentZone.Width || y < 0 || y >= CurrentZone.Height)
            return false;

        var middleTile = CurrentZone.GetTile(x, y, 1);

        // No tile in middle layer = walkable (check for 0xFFFF, 0, or out of range)
        if (middleTile == 0xFFFF || middleTile == 0) return true;
        if (middleTile >= _gameData.Tiles.Count) return true;

        var tile = _gameData.Tiles[middleTile];

        // Floor tiles are walkable, objects block unless draggable
        if (tile.IsFloor) return true;
        if (tile.IsDraggable) return true;
        if (tile.IsObject) return false;

        // If no specific flags, assume walkable
        return true;
    }

    private void TryPush(int x, int y, int dx, int dy)
    {
        if (CurrentZone == null) return;

        var middleTile = CurrentZone.GetTile(x, y, 1);
        if (middleTile == 0xFFFF || middleTile >= _gameData.Tiles.Count) return;

        var tile = _gameData.Tiles[middleTile];
        if (!tile.IsDraggable) return;

        // Check if we can push to the destination
        int pushX = x + dx;
        int pushY = y + dy;

        if (pushX < 0 || pushX >= CurrentZone.Width || pushY < 0 || pushY >= CurrentZone.Height)
            return;

        var destTile = CurrentZone.GetTile(pushX, pushY, 1);
        if (destTile != 0xFFFF && destTile < _gameData.Tiles.Count)
        {
            if (_gameData.Tiles[destTile].IsObject)
                return; // Can't push into another object
        }

        // Push the object
        CurrentZone.SetTile(pushX, pushY, 1, middleTile);
        CurrentZone.SetTile(x, y, 1, 0xFFFF);

        // Move player into the vacated spot
        PlayerX = x;
        PlayerY = y;

        Console.WriteLine($"Pushed object to ({pushX}, {pushY})");
    }

    private void TryZoneTransition(int dx, int dy)
    {
        // Check for door/teleporter objects at player position
        if (CurrentZone == null) return;

        foreach (var obj in CurrentZone.Objects)
        {
            if (obj.X != PlayerX || obj.Y != PlayerY) continue;

            if (obj.Type == ZoneObjectType.DoorEntrance ||
                obj.Type == ZoneObjectType.DoorExit ||
                obj.Type == ZoneObjectType.Teleporter)
            {
                if (obj.Argument > 0 && obj.Argument < _gameData.Zones.Count)
                {
                    Console.WriteLine($"Door transition to zone {obj.Argument}");
                    LoadZone(obj.Argument);
                    return;
                }
            }
        }
    }

    private void CheckPositionTriggers()
    {
        if (CurrentZone == null) return;

        foreach (var obj in CurrentZone.Objects)
        {
            if (obj.X != PlayerX || obj.Y != PlayerY) continue;

            switch (obj.Type)
            {
                case ZoneObjectType.CrateItem:
                    if (obj.Argument > 0)
                    {
                        Inventory.Add(obj.Argument);
                        Console.WriteLine($"Picked up item {obj.Argument}");
                        // Remove from zone (mark as collected)
                        obj.Type = ZoneObjectType.Unknown;
                    }
                    break;

                case ZoneObjectType.CrateWeapon:
                    if (obj.Argument > 0)
                    {
                        Console.WriteLine($"Picked up weapon {obj.Argument}");
                        obj.Type = ZoneObjectType.Unknown;
                    }
                    break;

                case ZoneObjectType.DoorEntrance:
                case ZoneObjectType.DoorExit:
                case ZoneObjectType.Teleporter:
                    if (obj.Argument > 0 && obj.Argument < _gameData.Zones.Count)
                    {
                        Console.WriteLine($"Entering zone {obj.Argument}");
                        LoadZone(obj.Argument);
                        return;
                    }
                    break;
            }
        }
    }

    public void Interact()
    {
        if (CurrentZone == null) return;

        // Get position in front of player
        int targetX = PlayerX;
        int targetY = PlayerY;

        switch (PlayerDirection)
        {
            case Direction.Up: targetY--; break;
            case Direction.Down: targetY++; break;
            case Direction.Left: targetX--; break;
            case Direction.Right: targetX++; break;
        }

        // Check for NPC
        var npc = ZoneNPCs.FirstOrDefault(n =>
            n.IsEnabled && n.IsAlive &&
            Math.Abs(n.X - targetX) <= 1 && Math.Abs(n.Y - targetY) <= 1);

        if (npc != null)
        {
            if (npc.IsHostile)
            {
                // Attack
                Attack(npc);
            }
            else
            {
                // Talk
                TalkTo(npc);
            }
            return;
        }

        // Check for interactable objects
        foreach (var obj in CurrentZone.Objects)
        {
            if (Math.Abs(obj.X - targetX) <= 1 && Math.Abs(obj.Y - targetY) <= 1)
            {
                if (obj.Type == ZoneObjectType.Lock)
                {
                    // Try to unlock with selected item
                    if (SelectedItem.HasValue)
                    {
                        Console.WriteLine($"Trying to unlock with item {SelectedItem.Value}");
                        // TODO: Check if item unlocks this lock
                    }
                }
            }
        }
    }

    private void Attack(NPC npc)
    {
        Console.WriteLine($"Attacking {npc.Name}!");
        npc.Health -= 10;
        if (npc.Health <= 0)
        {
            npc.IsAlive = false;
            Console.WriteLine($"{npc.Name} defeated!");
        }
    }

    private void TalkTo(NPC npc)
    {
        Console.WriteLine($"Talking to {npc.Name}");

        // Check if NPC has an item to give
        if (npc.CarriedItemId.HasValue && !npc.HasGivenItem)
        {
            Inventory.Add(npc.CarriedItemId.Value);
            npc.HasGivenItem = true;
            Console.WriteLine($"Received item {npc.CarriedItemId.Value} from {npc.Name}");
        }
    }

    public void Update(double deltaTime)
    {
        // Update NPCs
        foreach (var npc in ZoneNPCs.Where(n => n.IsEnabled && n.IsAlive))
        {
            npc.Update(deltaTime, this);
        }
    }
}

public enum Direction
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>
/// Runtime NPC state
/// </summary>
public class NPC
{
    public int CharacterId { get; set; }
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsAlive { get; set; }
    public bool IsHostile { get; set; }
    public int Health { get; set; } = 100;
    public int? CarriedItemId { get; set; }
    public bool HasGivenItem { get; set; }

    private double _moveTimer;
    private Random _random = new();

    public void Update(double deltaTime, GameEngine engine)
    {
        if (!IsHostile) return;

        _moveTimer += deltaTime;
        if (_moveTimer < 0.5) return;
        _moveTimer = 0;

        // Simple AI: move toward player if close, otherwise wander
        int dx = engine.PlayerX - X;
        int dy = engine.PlayerY - Y;
        int dist = Math.Abs(dx) + Math.Abs(dy);

        if (dist <= 5)
        {
            // Move toward player
            int moveX = Math.Sign(dx);
            int moveY = Math.Sign(dy);

            if (_random.Next(2) == 0)
                TryMove(moveX, 0, engine);
            else
                TryMove(0, moveY, engine);
        }
        else
        {
            // Random wander
            int dir = _random.Next(4);
            switch (dir)
            {
                case 0: TryMove(0, -1, engine); break;
                case 1: TryMove(0, 1, engine); break;
                case 2: TryMove(-1, 0, engine); break;
                case 3: TryMove(1, 0, engine); break;
            }
        }
    }

    private void TryMove(int dx, int dy, GameEngine engine)
    {
        if (engine.CurrentZone == null) return;

        int newX = X + dx;
        int newY = Y + dy;

        if (newX < 0 || newX >= engine.CurrentZone.Width ||
            newY < 0 || newY >= engine.CurrentZone.Height)
            return;

        // Check collision with player
        if (newX == engine.PlayerX && newY == engine.PlayerY)
            return;

        // Check collision with other NPCs
        if (engine.ZoneNPCs.Any(n => n != this && n.IsEnabled && n.IsAlive && n.X == newX && n.Y == newY))
            return;

        X = newX;
        Y = newY;
    }
}
