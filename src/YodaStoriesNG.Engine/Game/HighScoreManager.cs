using System.Text.Json;
using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Manages high scores for Force Factor (Yoda) and Indy Quotient (Indy).
/// </summary>
public static class HighScoreManager
{
    private const int MaxScoresPerGame = 10;
    private static readonly string ScoreFilePath;

    public class HighScore
    {
        public int Score { get; set; }
        public string Rating { get; set; } = "";
        public DateTime Date { get; set; }
        public WorldSize WorldSize { get; set; }
        public TimeSpan Time { get; set; }
    }

    public class HighScoreData
    {
        public List<HighScore> YodaScores { get; set; } = new();
        public List<HighScore> IndyScores { get; set; } = new();
    }

    private static HighScoreData _scores = new();

    static HighScoreManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var gameDir = Path.Combine(appData, "YodaStoriesNG");
        Directory.CreateDirectory(gameDir);
        ScoreFilePath = Path.Combine(gameDir, "highscores.json");
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(ScoreFilePath))
            {
                var json = File.ReadAllText(ScoreFilePath);
                _scores = JsonSerializer.Deserialize<HighScoreData>(json) ?? new HighScoreData();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HighScores] Failed to load: {ex.Message}");
            _scores = new HighScoreData();
        }
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_scores, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ScoreFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HighScores] Failed to save: {ex.Message}");
        }
    }

    public static void AddScore(GameType gameType, int score, string rating, WorldSize worldSize, TimeSpan time)
    {
        var newScore = new HighScore
        {
            Score = score,
            Rating = rating,
            Date = DateTime.Now,
            WorldSize = worldSize,
            Time = time
        };

        var list = gameType == GameType.IndianaJones ? _scores.IndyScores : _scores.YodaScores;
        list.Add(newScore);
        list.Sort((a, b) => b.Score.CompareTo(a.Score)); // Sort descending

        // Keep only top scores
        while (list.Count > MaxScoresPerGame)
            list.RemoveAt(list.Count - 1);

        Save();
    }

    public static List<HighScore> GetScores(GameType gameType)
    {
        return gameType == GameType.IndianaJones ? _scores.IndyScores : _scores.YodaScores;
    }

    public static int GetHighScore(GameType gameType)
    {
        var list = gameType == GameType.IndianaJones ? _scores.IndyScores : _scores.YodaScores;
        return list.Count > 0 ? list[0].Score : 0;
    }
}
