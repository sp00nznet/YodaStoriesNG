using System.Text.Json;
using System.Text.Json.Serialization;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Local diagnostic files; logging failures must never interrupt gameplay.</summary>
public sealed class BotDebugLog : IDisposable
{
    private StreamWriter? _writer;
    private StreamWriter? _readable;
    private TextWriter? _originalConsole;
    private TextWriter? _redirectedConsole;

    private sealed class SafeLogWriter : TextWriter
    {
        private readonly StreamWriter _file;
        private readonly Action<Exception> _failed;
        private bool _disabled;
        private void SafeWrite(System.Action write)
        {
            if (_disabled) return;
            try { lock(_file) write(); }
            catch(Exception e) when(e is IOException or ObjectDisposedException)
            { _disabled=true; _failed(e); }
        }
        public SafeLogWriter(StreamWriter file,Action<Exception> failed) { _file=file; _failed=failed; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => SafeWrite(()=>_file.Write(value));
        public override void Write(string? value) => SafeWrite(()=>_file.Write(value));
        public override void WriteLine(string? value) => SafeWrite(()=>_file.WriteLine(value));
        public override void Flush() => SafeWrite(()=>_file.Flush());
    }
    private readonly GameData _data;
    private readonly GameState _state;
    private readonly WorldGenerator _world;
    private DateTime _lastSnapshot = DateTime.MinValue;
    private int _snapshotNumber;
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = false, IncludeFields = true, Converters = { new JsonStringEnumConverter() } };
    public string? DirectoryPath { get; private set; }

    public BotDebugLog(GameData data, GameState state, WorldGenerator world, string? logRoot = null)
    {
        _data=data; _state=state; _world=world;
        try
        {
            DirectoryPath=Path.Combine(logRoot ?? Path.Combine(SaveGameManager.GetSaveDirectory(),"bot-debug"),
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(DirectoryPath);
            _writer=new StreamWriter(Path.Combine(DirectoryPath,"events.jsonl")) { AutoFlush=true };
            _readable=new StreamWriter(Path.Combine(DirectoryPath,"actions.log")) { AutoFlush=true };
            _originalConsole=Console.Out;
            _originalConsole.WriteLine($"[BOT] Log: {Path.Combine(DirectoryPath,"actions.log")}");
            Console.SetOut(new SafeLogWriter(_readable,Disable));
            _redirectedConsole=Console.Out;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(DirectoryPath)!,"latest-session.txt"),DirectoryPath);

            Record("session-start",new { SchemaVersion=1, Game=data.GameType,
                Version=typeof(BotDebugLog).Assembly.GetName().Version?.ToString(),
                Seed=world.CurrentWorld?.Seed, WorldSize=world.CurrentWorld?.GenerationSize,
                Planet=world.CurrentWorld?.Planet,
                Reproduce=world.CurrentWorld?.Seed is int seed ? $"--seed {seed}" : "Seed unavailable for this older world",
                RequestedSeed=Environment.GetEnvironmentVariable("YSNG_SEED") });
            Snapshot("session-start",true,true);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Disable(e); }
    }

    public void Record(string kind,object? detail)
    {
        if(_writer==null) return;
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(new { Time=DateTime.UtcNow, Event=kind,
                Zone=_state.CurrentZoneId, Player=new { X=_state.PlayerX,Y=_state.PlayerY, Facing=_state.PlayerDirection },
                Inventory=_state.Inventory.ToArray(), SelectedItem=_state.SelectedItem,
                Weapon=_state.SelectedWeapon, Health=_state.Health, Detail=detail },Options));
            if (_readable is { } readable) lock(readable) readable.WriteLine($"{DateTime.UtcNow:O} [{kind}] zone={_state.CurrentZoneId} pos=({_state.PlayerX},{_state.PlayerY}) " +
                $"inventory=[{string.Join(',',_state.Inventory)}] selected={_state.SelectedItem} weapon={_state.SelectedWeapon} " +
                JsonSerializer.Serialize(detail,Options));
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { Disable(e); }
    }

    private object Area(Zone z) => new { z.Id,z.Width,z.Height,z.Type,z.Planet,
        Layers=Enumerable.Range(0,3).Select(layer=>Enumerable.Range(0,z.Height)
            .Select(y=>Enumerable.Range(0,z.Width).Select(x=>z.GetTile(x,y,layer)).ToArray()).ToArray()).ToArray(),
        Hotspots=z.Objects.Select(h=>new { h.Type,h.X,h.Y,h.Argument,h.FileArgument,h.Enabled }).ToArray(),
        Monsters=z.Monsters.Select(m=>new { m.CharacterId,m.X,m.Y,m.Loot,m.DropsLoot,m.Waypoints }).ToArray(),
        Scripts=z.Actions.Select((a,i)=>new { Index=i,
            Disabled=_state.DisabledActions.Contains((z.Id,i)),
            Conditions=a.Conditions.Select(c=>new { c.Opcode,c.Arguments }).ToArray(),
            Instructions=a.Instructions.Select(n=>new { n.Opcode,n.Arguments,n.Text }).ToArray() }).ToArray() };

    public void Snapshot(string reason,bool force=false,bool fullWorld=false,object? planner=null)
    {
        if(_writer==null || DirectoryPath==null || !force && (DateTime.UtcNow-_lastSnapshot).TotalSeconds<5) return;
        try
        {
            _lastSnapshot=DateTime.UtcNow;
            var world=_world.CurrentWorld;
            var zones=fullWorld ? _data.Zones.Where(z=>world==null || world.Sectors.ContainsKey(z.Id) ||
                world.Connections.ContainsKey(z.Id) || world.DagobahZones.Contains(z.Id)).ToList()
                : _state.CurrentZone is { } current ? new List<Zone> { current } : new List<Zone>();
            string file=$"snapshot-{++_snapshotNumber:0000}.json";
            var payload=new { SchemaVersion=1,Time=DateTime.UtcNow,Reason=reason,
                Player=new { _state.SmoothMovementEnabled,_state.CurrentZoneId,_state.PlayerX,_state.PlayerY,_state.PlayerDirection,_state.HeroVisible,
                    _state.IsPaused,_state.IsGameOver,_state.PreviousZoneId },
                Inventory=_state.Inventory.ToArray(),_state.SelectedItem,_state.SelectedWeapon,
                _state.Counters,_state.ZoneRandom,_state.ZoneRolls,_state.Variables,
                DisabledActions=_state.DisabledActions.Select(a=>new { a.Zone,a.Action }).ToArray(),
                DefeatedMonsters=_state.DefeatedMonsters.ToArray(),DisabledMonsters=_state.DisabledMonsters.ToArray(),
                Collected=_state.CollectedObjects.ToArray(),Solved=_state.SolvedZones.ToArray(),Visited=_state.VisitedZones.ToArray(),
                Actors=_state.ZoneNPCs.Select(n=>new { n.CharacterId,n.NativeMonsterIndex,n.X,n.Y,n.StartX,n.StartY,n.Health,
                    n.IsEnabled,n.IsHostile,n.IsInvulnerable,n.Behavior,n.Direction,n.AnimationFrame,n.DropsLoot,n.LootItemId }).ToArray(),
                World=world==null?null:new { world.Seed,world.GenerationSize,world.Planet,world.StartingItemId,world.StartingZoneId,
                    world.LandingZoneId,world.ObjectiveZoneId,world.RoomParents,world.Connections,
                    Sectors=world.Sectors.Select(p=>new { Zone=p.Key,RootZone=p.Value.Zone,p.Value.Type,p.Value.RequiredItem,
                        p.Value.FindItem,p.Value.Npc,p.Value.UsedAlternateStrain,p.Value.AdditionalRequiredItem,p.Value.AdditionalGainItem,p.Value.Solved,p.Value.PuzzleIndex }).ToArray(),
                    Mission=world.Mission==null?null:new { world.Mission.Id,world.Mission.Name,world.Mission.CurrentStep,world.Mission.IsCompleted } },
                Areas=zones.Select(Area).ToArray(),
                TileFlags=_data.Tiles.Select((t,i)=>new { Id=i,t.Flags }).ToArray(), Planner=planner };
            File.WriteAllText(Path.Combine(DirectoryPath,file),JsonSerializer.Serialize(payload,Options));
            Record("snapshot",new { Reason=reason,File=file });
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { Disable(e); }
    }
    private void Disable(Exception e)
    {
        RestoreConsole();
        Console.WriteLine($"[BOT] Debug logging unavailable: {e.Message}");
        Dispose();
    }
    private void RestoreConsole()
    {
        if (_originalConsole!=null && ReferenceEquals(Console.Out,_redirectedConsole))
            Console.SetOut(_originalConsole);
        _redirectedConsole=null;
    }
    public void Dispose()
    {
        RestoreConsole();
        try { _writer?.Dispose(); } catch(IOException) { }
        try { _readable?.Dispose(); } catch(IOException) { }
        _writer=null; _readable=null;
    }
}
