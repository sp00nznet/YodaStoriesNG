using System.Diagnostics;
using System.Text.Json;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    private bool _headless;

    /// <summary>Runs the real simulation without SDL, rendering, audio or input polling.</summary>
    public static int RunHeadlessBot(string dataFile, double speed, double seconds,
        WorldSize size, bool smooth, string? logRoot, string? resumeFile = null)
    {
        using var engine = new GameEngine(Path.GetDirectoryName(Path.GetFullPath(dataFile))!)
        {
            _headless = true, _showingTitleScreen = false,
            _selectedWorldSize = size, _smoothMovement = smooth, _smoothMovementRequested = smooth,
            _gameData = new DtaParser().Parse(dataFile)
        };
        var checkpoint = resumeFile == null ? null : ReadBotCheckpoint(resumeFile, dataFile);
        if (checkpoint != null)
        {
            Environment.SetEnvironmentVariable("YSNG_SEED", checkpoint.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            size = checkpoint.Size;
            engine._selectedWorldSize = size;
        }
        engine.CaptureOriginalAreas();
        engine.InitializeActionExecutor();
        engine.StartNewGame();
        if (checkpoint != null) engine.RestoreBotCheckpoint(checkpoint);
        // Older checkpoints predate the native wandering behavior mapping.
        foreach(var npc in engine._state.ZoneNPCs)
            if(npc.CharacterId>=0 && npc.CharacterId<engine._gameData.Characters.Count &&
                engine._gameData.Characters[npc.CharacterId].MovementType==3) npc.Behavior=NPCBehavior.Wandering;
        engine._state.SmoothMovementEnabled = smooth;
        engine._bot!.DebugLogRoot = logRoot;
        bool cancelled = false;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled = true; };
        Console.CancelKeyPress += cancel;
        const double tick = 1.0 / 60.0;
        long steps = 0;
        var elapsed = Stopwatch.StartNew();
        double planningSeconds = 0;
        double nextCheckpoint = 0;
        int checkpointNumber = 0, checkpointZone = -1;
        try
        {
            engine.EnableBot();
            if (checkpoint != null)
            {
                // Earlier replays inferred returns from any matching room link.
                // The engine's recorded doorway history is authoritative.
                checkpoint.Intent.RoomReturns=checkpoint.DoorReturns.ToDictionary(p=>p.Key,p=>p.Value.Zone);
                checkpoint.Intent.DoorReturnPoints=new(checkpoint.DoorReturns);
                engine._bot!.RestoreReplayIntent(checkpoint.Intent);
            }
            if (checkpoint != null) engine._bot.TraceDebug("checkpoint-resumed", new { File=resumeFile, checkpoint.SimulationSeconds, Planner="fresh" });
            engine._bot.TraceDebug("headless-start", new { Speed=speed, Seconds=seconds,
                FixedStep=tick, SmoothMovement=smooth, WorldSize=size.ToString(),
                Seed=engine._worldGenerator?.CurrentWorld?.Seed });
            while (!cancelled && steps * tick < seconds && engine.IsBotRunning &&
                   !engine._state.IsGameOver && !engine._state.IsGameWon)
            {
                // Never enlarge the timestep to speed up the game: run more normal ticks.
                engine.Update(Math.Min(tick, seconds - steps * tick));
                steps++;
                if ((steps*tick >= nextCheckpoint || checkpointZone != engine._state.CurrentZoneId) && engine.CanCheckpointBot())
                {
                    if (engine._bot.DebugLogDirectory is { } checkpointDirectory)
                    {
                        string path = Path.Combine(checkpointDirectory,$"checkpoint-{++checkpointNumber:0000}.json.gz");
                        engine.SaveBotCheckpoint(path, dataFile, (checkpoint?.SimulationSeconds ?? 0)+steps*tick);
                        engine._bot.TraceDebug("checkpoint-saved", new { File=path, SimulationSeconds=steps*tick });
                    }
                    nextCheckpoint=steps*tick+30;
                    checkpointZone=engine._state.CurrentZoneId;
                }
                planningSeconds += engine._bot.ConsumePlanningSeconds();
                if (speed > 0)
                {
                    double wait = steps * tick / speed - (elapsed.Elapsed.TotalSeconds - planningSeconds);
                    while (wait > 0 && !cancelled)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.01)));
                        wait = steps * tick / speed - (elapsed.Elapsed.TotalSeconds - planningSeconds);
                    }
                }
            }
            // Preserve the exact stopped state too; a short failure may happen
            // before the next periodic checkpoint becomes due.
            if(engine.CanCheckpointBot() && engine._bot.DebugLogDirectory is { } stoppedDirectory)
            {
                string path=Path.Combine(stoppedDirectory,$"checkpoint-{++checkpointNumber:0000}.json.gz");
                engine.SaveBotCheckpoint(path,dataFile,(checkpoint?.SimulationSeconds ?? 0)+steps*tick);
                engine._bot.TraceDebug("checkpoint-saved",new { File=path,SimulationSeconds=steps*tick,Reason="run-finished" });
            }
            string reason = engine._state.IsGameWon ? "mission-complete" :
                engine._state.IsGameOver ? "game-over" : cancelled ? "cancelled" :
                !engine.IsBotRunning ? "planner-stopped" : "simulation-limit";
            var summary = new { Reason=reason, SimulationSeconds=Math.Min(seconds,steps*tick),
                WallSeconds=elapsed.Elapsed.TotalSeconds, PlanningSeconds=planningSeconds,
                RequestedSpeed=speed, FixedStep=tick, SmoothMovement=smooth,
                Seed=engine._worldGenerator?.CurrentWorld?.Seed, WorldSize=size.ToString(),
                Zone=engine._state.CurrentZoneId, engine._state.PlayerX, engine._state.PlayerY,
                MissionStep=engine._worldGenerator?.CurrentWorld?.Mission?.CurrentStep,
                Task=engine._bot.ObjectiveDescription ?? engine._bot.CurrentTask, LogDirectory=engine._bot.DebugLogDirectory };
            engine._bot.TraceDebug("headless-finished", summary);
            engine._bot.Stop();
            if (summary.LogDirectory is { } directory)
            {
                File.WriteAllText(Path.Combine(directory,"run-summary.json"),
                    JsonSerializer.Serialize(summary,new JsonSerializerOptions { WriteIndented=true }));
                Console.WriteLine($"Actions: {Path.Combine(directory,"actions.log")}");
            }
            Console.WriteLine(JsonSerializer.Serialize(summary));
            return reason == "mission-complete" ? 0 : reason == "cancelled" ? 130 :
                reason == "simulation-limit" ? 3 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
