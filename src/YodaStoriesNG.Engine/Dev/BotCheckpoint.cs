using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Bot;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    // Developer replay checkpoints deliberately exclude planner caches. Restoring starts a
    // fresh planner against the saved game, so changes to planning code can be evaluated.
    private sealed class BotCheckpoint
    {
        public int Version { get; set; } = 2;
        public MissionSolver.ReplayIntent Intent { get; set; } = new();
        public string DataHash { get; set; } = "";
        public int Seed { get; set; }
        public WorldSize Size { get; set; }
        public double SimulationSeconds { get; set; }
        public GameState State { get; set; } = new();
        public List<CheckpointArea> Areas { get; set; } = new();
        public int[] LaidDown { get; set; } = [];
        public int[] SolvedSectors { get; set; } = [];
        public int MissionStep { get; set; }
        public bool MissionCompleted { get; set; }
        public Dictionary<int,(int Zone,int X,int Y)> DoorReturns { get; set; } = new();
    }

    private sealed class CheckpointArea
    {
        public int Id { get; set; }
        public ushort[] Tiles { get; set; } = [];
        public List<ZoneObject> Objects { get; set; } = new();
        public List<ZoneMonster> Monsters { get; set; } = new();
    }

    private static readonly JsonSerializerOptions CheckpointOptions = CreateCheckpointOptions();
    private static JsonSerializerOptions CreateCheckpointOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(GameState))
                foreach (var property in info.Properties.Where(p => p.Name == nameof(GameState.CurrentZone)).ToArray())
                    info.Properties.Remove(property); // Zone grids are multidimensional; saved separately below.
        });
        return new JsonSerializerOptions { IncludeFields=true, TypeInfoResolver=resolver };
    }

    private bool CanCheckpointBot() => !BotActionPending() && _actionExecutor?.HasDeferredDrops != true && _npcSteps.Count == 0 &&
        _pendingMovementAction == null && !_state.IsAttacking && !_state.IsPaused &&
        !_state.IsGameOver && !_state.IsGameWon && _state.HeroVisible && _state.Projectiles.Count == 0;

    private void SaveBotCheckpoint(string path, string dataFile, double simulationSeconds)
    {
        var world = _worldGenerator!.CurrentWorld!;
        if (world.Seed is not int seed) return;
        var checkpoint = new BotCheckpoint
        {
            Intent=_bot!.CaptureReplayIntent(),
            DataHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataFile))),
            Seed=seed, Size=world.GenerationSize, SimulationSeconds=simulationSeconds, State=_state,
            LaidDown=_sectorPuzzles?.CheckpointLaidDown() ?? [],
            SolvedSectors=world.Sectors.Where(p=>p.Value.Solved).Select(p=>p.Key).ToArray(),
            MissionStep=world.Mission?.CurrentStep ?? 0, MissionCompleted=world.Mission?.IsCompleted ?? false,
            DoorReturns=new(_doorReturnPoints),
            Areas=_gameData!.Zones.Select(z=>new CheckpointArea
            {
                Id=z.Id, Tiles=z.TileGrid.Cast<ushort>().ToArray(), Objects=z.Objects, Monsters=z.Monsters
            }).ToList()
        };
        string temporary=path+".tmp";
        using (var file=File.Create(temporary))
        using (var compressed=new GZipStream(file,CompressionLevel.Fastest))
            JsonSerializer.Serialize(compressed,checkpoint,CheckpointOptions);
        File.Move(temporary,path,true);
    }

    private static BotCheckpoint ReadBotCheckpoint(string path, string dataFile)
    {
        using var file=File.OpenRead(path);
        using var compressed=new GZipStream(file,CompressionMode.Decompress);
        var checkpoint=JsonSerializer.Deserialize<BotCheckpoint>(compressed,CheckpointOptions)
            ?? throw new InvalidDataException("Empty bot checkpoint.");
        if (checkpoint.Version != 2 || checkpoint.DataHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataFile))))
            throw new InvalidDataException("Checkpoint version or game data does not match.");
        return checkpoint;
    }

    private void RestoreBotCheckpoint(BotCheckpoint checkpoint)
    {
        // Regeneration supplies immutable scripts and mission structure. Never LoadZone
        // here: doing so would execute entrance scripts a second time.
        foreach (var area in checkpoint.Areas)
        {
            var zone=_gameData!.Zones.Single(z=>z.Id==area.Id);
            if (area.Tiles.Length != zone.TileGrid.Length) throw new InvalidDataException("Checkpoint area size differs.");
            int i=0;
            for (int a=0;a<zone.TileGrid.GetLength(0);a++)
                for (int b=0;b<zone.TileGrid.GetLength(1);b++)
                    for (int c=0;c<zone.TileGrid.GetLength(2);c++)
                        zone.TileGrid[a,b,c]=area.Tiles[i++];
            zone.Objects=area.Objects;
            zone.Monsters=area.Monsters;
        }
        foreach (var property in typeof(GameState).GetProperties().Where(p=>p.CanWrite && p.Name!=nameof(GameState.CurrentZone)))
            property.SetValue(_state,property.GetValue(checkpoint.State));
        _state.CurrentZone=_gameData!.Zones.Single(z=>z.Id==_state.CurrentZoneId);
        _sectorPuzzles?.RestoreLaidDown(checkpoint.LaidDown);
        var world=_worldGenerator!.CurrentWorld!;
        foreach (var pair in world.Sectors) pair.Value.Solved=checkpoint.SolvedSectors.Contains(pair.Key);
        if (world.Mission is { } mission)
        { mission.CurrentStep=checkpoint.MissionStep; mission.IsCompleted=checkpoint.MissionCompleted; }
        _doorReturnPoints.Clear();
        foreach (var pair in checkpoint.DoorReturns) _doorReturnPoints.Add(pair.Key,pair.Value);
        CancelSmoothMovement();
        _messages.Clear();
        _actionExecutor!.ResetForZone();

    }
}
