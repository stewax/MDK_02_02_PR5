namespace LicenseClient;

/// <summary>Снимок состояния клиента для отображения.</summary>
public sealed record ClientSnapshot(
    string Status,
    ConsoleColor StatusColor,
    string? Code,
    DateTime? ConnectedAt,
    string? LocalIp,
    int? LocalPort,
    int? RemainingSeconds,
    DateTime? LastCheck,
    string? LastCheckResult,
    DateTime? RetryAt,
    IReadOnlyList<(DateTime Time, string Text, ConsoleColor Color)> Events);

/// <summary>Потокобезопасное состояние клиента: текущее подключение, код и журнал событий.</summary>
public sealed class ClientState
{
    private const int MaxEvents = 8;
    private readonly object _sync = new();
    private readonly List<(DateTime, string, ConsoleColor)> _events = new();

    private string _status = "Запуск";
    private ConsoleColor _statusColor = ConsoleColor.Gray;
    private string? _code;
    private DateTime? _connectedAt;
    private string? _localIp;
    private int? _localPort;
    private int? _remaining;
    private DateTime _remainingAt;
    private DateTime? _lastCheck;
    private string? _lastCheckResult;
    private DateTime? _retryAt;

    /// <summary>Вызывается при добавлении события (используется в режиме без интерактивной консоли).</summary>
    public event Action<DateTime, string, ConsoleColor>? EventLogged;

    public void SetStatus(string status, ConsoleColor color, bool logEvent = true)
    {
        lock (_sync)
        {
            _status = status;
            _statusColor = color;
        }
        if (logEvent)
            AddEvent(status, color);
    }

    public void AddEvent(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        var time = DateTime.Now;
        lock (_sync)
        {
            _events.Add((time, text, color));
            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }
        EventLogged?.Invoke(time, text, color);
    }

    /// <summary>Сбрасывает данные предыдущей сессии перед новым подключением.</summary>
    public void ResetSession()
    {
        lock (_sync)
        {
            _code = null;
            _connectedAt = null;
            _localIp = null;
            _localPort = null;
            _remaining = null;
            _lastCheck = null;
            _lastCheckResult = null;
            _retryAt = null;
        }
    }

    public void SetConnected(DateTime connectedAt, string localIp, int localPort)
    {
        lock (_sync)
        {
            _connectedAt = connectedAt;
            _localIp = localIp;
            _localPort = localPort;
        }
    }

    public void SetCode(string code, int? remainingSeconds)
    {
        lock (_sync)
        {
            _code = code;
            SetRemaining(remainingSeconds);
        }
    }

    public void SetCheckResult(string result, int? remainingSeconds)
    {
        lock (_sync)
        {
            _lastCheck = DateTime.Now;
            _lastCheckResult = result;
            SetRemaining(remainingSeconds);
        }
    }

    public void SetRetry(TimeSpan delay)
    {
        lock (_sync)
            _retryAt = DateTime.Now + delay;
    }

    public ClientSnapshot Snapshot()
    {
        lock (_sync)
        {
            int? remaining = _remaining is null
                ? null
                : Math.Max(0, (int)Math.Ceiling(_remaining.Value - (DateTime.Now - _remainingAt).TotalSeconds));

            return new ClientSnapshot(_status, _statusColor, _code, _connectedAt, _localIp, _localPort,
                remaining, _lastCheck, _lastCheckResult, _retryAt, _events.ToList());
        }
    }

    // Вызывается под блокировкой _sync.
    private void SetRemaining(int? remainingSeconds)
    {
        if (remainingSeconds is null)
            return;
        _remaining = remainingSeconds;
        _remainingAt = DateTime.Now;
    }
}
