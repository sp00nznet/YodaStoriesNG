namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Message type for different display styles.
/// </summary>
public enum MessageType
{
    Info,       // General information
    Pickup,     // Item pickup notification
    Combat,     // Combat messages
    Dialogue,   // NPC dialogue
    System      // System messages
}

/// <summary>
/// Represents a message to display on screen.
/// </summary>
public class GameMessage
{
    public string Text { get; set; } = string.Empty;
    public MessageType Type { get; set; }
    public double Duration { get; set; }
    public double TimeRemaining { get; set; }
    public bool IsExpired => TimeRemaining <= 0;
}

/// <summary>
/// Manages game messages and dialogue display.
/// </summary>
public class MessageSystem
{
    private readonly List<GameMessage> _messages = new();
    private readonly Queue<GameMessage> _messageQueue = new();
    private GameMessage? _currentDialogue;
    private const int MaxVisibleMessages = 4;
    private const double DefaultMessageDuration = 3.0;
    private const double PickupMessageDuration = 2.0;
    private const double CombatMessageDuration = 1.5;

    /// <summary>
    /// Adds a message to be displayed.
    /// </summary>
    public void ShowMessage(string text, MessageType type = MessageType.Info)
    {
        var duration = type switch
        {
            MessageType.Pickup => PickupMessageDuration,
            MessageType.Combat => CombatMessageDuration,
            MessageType.Dialogue => 5.0,
            _ => DefaultMessageDuration
        };

        var message = new GameMessage
        {
            Text = text,
            Type = type,
            Duration = duration,
            TimeRemaining = duration
        };

        if (type == MessageType.Dialogue)
        {
            // Dialogue messages queue up
            _messageQueue.Enqueue(message);
            if (_currentDialogue == null)
                _currentDialogue = _messageQueue.Dequeue();
        }
        else
        {
            // Regular messages show immediately
            _messages.Add(message);

            // Limit visible messages
            while (_messages.Count > MaxVisibleMessages)
            {
                _messages.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Shows a pickup notification.
    /// </summary>
    public void ShowPickup(string itemName)
    {
        ShowMessage($"Picked up: {itemName}", MessageType.Pickup);
    }

    /// <summary>
    /// Shows a combat message.
    /// </summary>
    public void ShowCombat(string text)
    {
        ShowMessage(text, MessageType.Combat);
    }

    /// <summary>
    /// Shows NPC dialogue.
    /// </summary>
    public void ShowDialogue(string npcName, string text)
    {
        ShowMessage($"{npcName}: \"{text}\"", MessageType.Dialogue);
    }

    /// <summary>
    /// Advances to next dialogue if current one is dismissed.
    /// </summary>
    public void DismissDialogue()
    {
        if (_currentDialogue != null)
        {
            _currentDialogue = _messageQueue.Count > 0 ? _messageQueue.Dequeue() : null;
        }
    }

    /// <summary>
    /// Updates message timers.
    /// </summary>
    public void Update(double deltaTime)
    {
        // Update regular messages
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            _messages[i].TimeRemaining -= deltaTime;
            if (_messages[i].IsExpired)
            {
                _messages.RemoveAt(i);
            }
        }

        // Update dialogue
        if (_currentDialogue != null)
        {
            _currentDialogue.TimeRemaining -= deltaTime;
            if (_currentDialogue.IsExpired)
            {
                _currentDialogue = _messageQueue.Count > 0 ? _messageQueue.Dequeue() : null;
            }
        }
    }

    /// <summary>
    /// Gets all currently visible messages.
    /// </summary>
    public IReadOnlyList<GameMessage> GetMessages() => _messages;

    /// <summary>
    /// Gets the current dialogue message.
    /// </summary>
    public GameMessage? CurrentDialogue => _currentDialogue;

    /// <summary>
    /// Checks if there's an active dialogue.
    /// </summary>
    public bool HasDialogue => _currentDialogue != null;

    /// <summary>
    /// Clears all messages.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        _messageQueue.Clear();
        _currentDialogue = null;
    }
}
