using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LicenseClient;

/// <summary>
/// Логика клиента менеджера лицензий:
/// подключение, получение и подтверждение кода, периодическая проверка,
/// автоматическое переподключение для обновления кода.
/// </summary>
public sealed class LicenseClientService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly ClientSettings _settings;
    private readonly ClientState _state;

    public LicenseClientService(ClientSettings settings, ClientState state)
    {
        _settings = settings;
        _state = state;
    }

    private TimeSpan Retry => TimeSpan.FromSeconds(_settings.RetrySeconds);

    /// <summary>Главный цикл: сессия за сессией, пока пользователь не завершит программу.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay = await RunSessionAsync(ct);
            if (ct.IsCancellationRequested)
                break;

            if (delay > TimeSpan.Zero)
            {
                _state.SetRetry(delay);
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Одна TCP-сессия. Возвращает паузу перед следующим подключением.</summary>
    private async Task<TimeSpan> RunSessionAsync(CancellationToken ct)
    {
        _state.ResetSession();
        _state.SetStatus($"Подключение к {_settings.ServerIp}:{_settings.ServerPort}...", ConsoleColor.Cyan);

        using var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(_settings.ServerIp, _settings.ServerPort, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _state.SetStatus("Сервер не отвечает", ConsoleColor.Red);
            return Retry;
        }
        catch (OperationCanceledException)
        {
            return TimeSpan.Zero;
        }
        catch (SocketException ex)
        {
            _state.SetStatus($"Не удалось подключиться к серверу: {ex.SocketErrorCode}", ConsoleColor.Red);
            return Retry;
        }

        var local = (IPEndPoint)tcp.Client.LocalEndPoint!;
        var localIp = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        _state.SetConnected(DateTime.Now, localIp.ToString(), local.Port);
        _state.AddEvent($"Соединение установлено, локальный адрес {localIp}:{local.Port}");

        var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Utf8NoBom);
        using var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true, NewLine = "\n" };

        try
        {
            // 1. Запрос уникального кода
            _state.SetStatus("Запрос уникального кода...", ConsoleColor.Cyan);
            await SendAsync(writer, new Message { Type = MessageTypes.GetCode });

            var response = await ReadWithTimeoutAsync(reader, ct);
            if (response is null)
                return ConnectionClosed();
            if (response.Type != MessageTypes.Code || string.IsNullOrEmpty(response.Code))
                return HandleServerNotice(response);

            string code = response.Code;
            _state.SetCode(code, response.RemainingSeconds);
            _state.AddEvent($"Получен уникальный код {code}", ConsoleColor.Green);

            // 2. Подтверждение кода
            await SendAsync(writer, new Message { Type = MessageTypes.Confirm, Code = code });
            response = await ReadWithTimeoutAsync(reader, ct);
            if (response is null)
                return ConnectionClosed();
            if (response.Type != MessageTypes.Confirmed)
                return HandleServerNotice(response);

            _state.SetCheckResult("подтверждён", response.RemainingSeconds);
            _state.SetStatus("Лицензия активна", ConsoleColor.Green);

            // 3. Периодическая проверка кода
            return await KeepAliveAsync(reader, writer, code, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Пользователь завершает программу — корректно прощаемся с сервером.
            try { await SendAsync(writer, new Message { Type = MessageTypes.Bye }); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            return TimeSpan.Zero;
        }
        catch (TimeoutException)
        {
            _state.SetStatus("Сервер не ответил вовремя", ConsoleColor.Red);
            return Retry;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _state.SetStatus("Соединение с сервером потеряно", ConsoleColor.Red);
            return Retry;
        }
    }

    /// <summary>
    /// Каждые CheckIntervalSeconds отправляет CHECK и одновременно принимает сообщения сервера
    /// (ответы OK и уведомления об отключении).
    /// </summary>
    private async Task<TimeSpan> KeepAliveAsync(StreamReader reader, StreamWriter writer, string code,
        CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_settings.CheckIntervalSeconds);
        var nextCheck = DateTime.Now + interval;
        Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

        while (true)
        {
            var wait = nextCheck - DateTime.Now;
            var delayTask = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, ct);

            if (await Task.WhenAny(readTask, delayTask) == readTask)
            {
                string? line = await readTask;
                if (line is null)
                    return ConnectionClosed();

                var message = Protocol.Deserialize(line);
                if (message is not null)
                {
                    switch (message.Type)
                    {
                        case MessageTypes.Ok:
                            _state.SetCheckResult("OK", message.RemainingSeconds);
                            break;
                        case MessageTypes.Confirmed:
                            break;
                        case MessageTypes.Error:
                            _state.AddEvent($"Ошибка сервера: {message.Text}", ConsoleColor.Yellow);
                            break;
                        default:
                            return HandleServerNotice(message);
                    }
                }

                readTask = reader.ReadLineAsync(ct).AsTask();
            }
            else
            {
                await delayTask; // пробрасывает отмену
                await SendAsync(writer, new Message { Type = MessageTypes.Check, Code = code });
                nextCheck = DateTime.Now + interval;
            }
        }
    }

    /// <summary>Обрабатывает отказ/уведомление сервера и возвращает паузу до переподключения.</summary>
    private TimeSpan HandleServerNotice(Message message)
    {
        string text = string.IsNullOrWhiteSpace(message.Text) ? message.Type : message.Text;
        switch (message.Type)
        {
            case MessageTypes.SessionExpired:
                _state.SetStatus("Время сессии истекло — обновление кода", ConsoleColor.Yellow);
                return TimeSpan.FromSeconds(1);

            case MessageTypes.Invalid:
            case MessageTypes.Expired:
                _state.SetStatus($"Код недействителен ({text}) — получение нового", ConsoleColor.Yellow);
                return TimeSpan.FromSeconds(1);

            case MessageTypes.NoSlots:
                _state.SetStatus(text, ConsoleColor.Yellow);
                return Retry;

            case MessageTypes.Blocked:
                _state.SetStatus($"ЗАБЛОКИРОВАН: {text}", ConsoleColor.Red);
                return Retry * 3;

            case MessageTypes.Kicked:
                _state.SetStatus($"Отключён сервером: {text}", ConsoleColor.Yellow);
                return Retry * 2;

            default:
                _state.SetStatus($"Неожиданный ответ сервера: {text}", ConsoleColor.Red);
                return Retry;
        }
    }

    private TimeSpan ConnectionClosed()
    {
        _state.SetStatus("Сервер закрыл соединение", ConsoleColor.Red);
        return Retry;
    }

    private static Task SendAsync(StreamWriter writer, Message message) =>
        writer.WriteLineAsync(Protocol.Serialize(message));

    /// <summary>Читает одно сообщение, ожидая не дольше ResponseTimeout. null — соединение закрыто.</summary>
    private static async Task<Message?> ReadWithTimeoutAsync(StreamReader reader, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ResponseTimeout);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            if (line is null)
                return null;

            var message = Protocol.Deserialize(line);
            if (message is not null)
                return message;
        }
    }
}
