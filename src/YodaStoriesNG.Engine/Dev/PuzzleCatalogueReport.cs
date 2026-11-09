using System.Text;
using System.Text.Json;
using YodaStoriesNG.Engine.Bot;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Dev;

public static class PuzzleCatalogueReport
{
    public static int Run(string dataFile,string directory)
    {
        var data=new DtaParser().Parse(dataFile);
        if(data.Zones.Count==0)
            throw new InvalidDataException("No area templates were parsed. Use unpacked DTA data; compressed SZDD DAW files must be expanded first.");
        var catalogue=PuzzleCatalogue.Compile(data);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"catalogue.json"),JsonSerializer.Serialize(catalogue,LocalPuzzleState.Json));
        var report=new StringBuilder($"# {catalogue.Game} puzzle catalogue\n\n");
        report.AppendLine($"{catalogue.PuzzleDefinitions} puzzle definitions; {catalogue.Templates.Length} area templates; {catalogue.Templates.Sum(t=>t.Actions.Length)} conditional actions.\n");
        report.AppendLine("These are contracts and solution guidelines extracted from native scripts. **Catalogue coverage is not solution coverage.** Random guards and generated required/find items remain parameters. A verified plan is bound to a generated checkpoint and must replay through the engine.\n");
        report.AppendLine("## Mechanics\n\n| Mechanic | Areas | Guideline |\n|---|---:|---|");
        foreach(var family in catalogue.Templates.SelectMany(t=>t.Mechanics.Select((m,i)=>(Mechanic:m,Guide:t.Guidelines[i])))
            .GroupBy(p=>p.Mechanic).OrderBy(g=>g.Key))
            report.AppendLine($"| {family.Key} | {family.Count()} | {family.First().Guide} |");
        report.AppendLine("\n## Area coverage\n\n| Area | Type | Mechanics | Actions | Unsupported opcodes |\n|---:|---|---|---:|---:|");
        foreach(var t in catalogue.Templates)
            report.AppendLine($"| {t.Zone} | {t.Type} | {string.Join(", ",t.Mechanics)} | {t.Actions.Length} | {t.Actions.Sum(a=>a.Unsupported.Length)} |");
        report.AppendLine("\n## Review queue\n");
        var unsupported=catalogue.Templates.SelectMany(t=>t.Actions.Where(a=>a.Unsupported.Length>0).Select(a=>(t.Zone,Action:a))).ToArray();
        if(unsupported.Length==0) report.AppendLine("No unknown opcode values were found. This does not establish semantic or puzzle coverage.");
        foreach(var entry in unsupported) report.AppendLine($"- Area {entry.Zone}, action {entry.Action.Index}: {string.Join(", ",entry.Action.Unsupported)}");
        report.AppendLine("\nThe offline solver currently searches deterministic local script interactions, pickups, internal doors, and cardinal push/pull moves. Live combat/moving actors are explicit prerequisites; exhausted search budgets remain unresolved. Definitions, full guards/effects, and producer dependencies are in `catalogue.json`.");
        File.WriteAllText(Path.Combine(directory,"README.md"),report.ToString());
        Console.WriteLine($"Puzzle catalogue: {catalogue.Templates.Length} templates, {catalogue.Templates.Sum(t=>t.Actions.Length)} actions, {unsupported.Length} actions with unsupported opcode values. {Path.GetFullPath(directory)}");
        return 0;
    }
}
