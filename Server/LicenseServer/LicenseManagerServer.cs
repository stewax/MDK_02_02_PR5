using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LicenseServer;

/// <summary>
/// TCP-сервер менеджера лицензий:
/// выдаёт уникальные коды, ограничивает число лицензий, проверяет чёрный список,
/// отключает клиентов по истечении времени сессии и по команде администратора.
/// </summary>
public sealed class LicenseManagerServer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Сколько ждать закрытия соединения клиентом после уведомления об отключении.</summary>
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
    private readonly CodeGenerator _codeGenerator = new();
    private readonly object _licenseLock = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _lastSessionId;

    public LicenseManagerServer(ServerSettings settings, Blacklist blacklist)
    {
        Settings = settings;
        Blacklist = blacklist;
    }

    public ServerSettings Settings { get; }
    public Blacklist Blacklist { get; }

    public IReadOnlyList<ClientSession> Sessions => _sessions.Values.OrderBy(s => s.Id).ToList();

    /// <summary>Количество занятых лицензий (сессий, которым выдан код).</summary>
    public int UsedLicenses => _sessions.Values.Count(s => s.Code is not null);

    public void Start()
    {
        _listener = new TcpListener(Settings.IpAddress, Settings.Port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(MonitorLoopAsync);
    }

    public async Task StopAsync()
    {
        foreach (var session in _sessions.Values)
            await DisconnectAsync(session, Notice(MessageTypes.Kicked, "Сервер остановлен"), "сервер остановлен");

        await Task.Delay(300);
        _cts.Cancel();
        _listener?.Stop();
        foreach (var session in _sessions.Values)
            session.Close();
    }

    // ------------------------------------------------------------------
    // Команды администратора
    // ------------------------------------------------------------------

    /// <summary>Ищет клиента по номеру в списке (5 или #5) либо по уникальному коду.</summary>
    public ClientSession? FindSession(string key)
    {
        key = key.Trim().TrimStart('#');
        if (int.TryParse(key, out int id))
            return _sessions.TryGetValue(id, out var byId) ? byId : null;

        return _sessions.Values.FirstOrDefault(s =>
            string.Equals(s.Code, key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> KickAsync(string key)
    {
        var session = FindSession(key);
        if (session is null)
            return false;

        await DisconnectAsync(session,
            Notice(MessageTypes.Kicked, "Вы отключены администратором сервера"),
            "отключён администратором");
        return true;
    }

    /// <summary>
    /// Добавляет клиента в чёрный список: по номеру/коду (берётся его IP) или по IP-адресу.
    /// Все подключённые клиенты с этим IP отключаются.
    /// </summary>
    public async Task<BanResult> BanAsync(string key)
    {
        string? ip = FindSession(key)?.Ip;
        if (ip is null && key.Contains('.') && IPAddress.TryParse(key, out var address))
            ip = address.ToString();
        if (ip is null)
            return new BanResult(null, false, 0);

        bool added = Blacklist.Add(ip);

        int disconnected = 0;
        foreach (var session in _sessions.Values.Where(s => s.Ip == ip && !s.IsClosing))
        {
            await DisconnectAsync(session,
                Notice(MessageTypes.Blocked, $"Ваш IP-адрес {ip} добавлен в чёрный список сервера"),
                "добавлен в чёрный список");
            disconnected++;
        }

        return new BanResult(ip, added, disconnected);
    }

    public bool Unban(string ip) => Blacklist.Remove(ip.Trim());

    // ------------------------------------------------------------------
    // Сетевая часть
    // ------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Error($"Ошибка приёма подключения: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(tcp));
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        ClientSession session;
        try
        {
            session = new ClientSession(Interlocked.Increment(ref _lastSessionId), tcp);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            tcp.Close();
            return;
        }

        _sessions[session.Id] = session;
        Log.Info($"Клиент #{session.Id} подключился: {session.Ip}:{session.Port}");

        try
        {
            using var reader = new StreamReader(session.Stream, Utf8NoBom);
            while (!_cts.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                    break; // клиент закрыл соединение

                // После уведомления об отключении входящие сообщения уже не обрабатываются,
                // только дочитываются, пока клиент не закроет соединение.
                if (session.IsClosing || string.IsNullOrWhiteSpace(line))
                    continue;

                var message = Protocol.Deserialize(line);
                if (message is null)
                {
                    await session.SendAsync(Notice(MessageTypes.Error, "Некорректный формат сообщения"));
                    continue;
                }

                if (!await ProcessMessageAsync(session, message))
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                       or OperationCanceledException or SocketException)
        {
            // Разрыв соединения — обрабатывается в finally.
        }
        finally
        {
            session.Close();
            if (_sessions.TryRemove(session.Id, out _))
            {
                string code = session.Code is null ? "" : $", код {session.Code}";
                Log.Warn($"Клиент #{session.Id} ({session.Ip}:{session.Port}{code}) отключён: " +
                         $"{session.CloseReason ?? "соединение закрыто клиентом"}. " +
                         $"Длительность подключения: {Format.Duration(session.Duration)}");
            }
        }
    }

    /// <summary>Обрабатывает сообщение клиента. Возвращает false, если соединение нужно закрыть.</summary>
    private async Task<bool> ProcessMessageAsync(ClientSession session, Message message)
    {
        switch (message.Type.Trim().ToUpperInvariant())
        {
            case MessageTypes.GetCode:
                await HandleGetCodeAsync(session);
                return true;

            case MessageTypes.Confirm:
                await HandleConfirmAsync(session, message);
                return true;

            case MessageTypes.Check:
                await HandleCheckAsync(session, message);
                return true;

            case MessageTypes.Bye:
                session.BeginClosing("клиент завершил работу");
                return false;

            default:
                await session.SendAsync(Notice(MessageTypes.Error, $"Неизвестная команда: {message.Type}"));
                return true;
        }
    }

    private async Task HandleGetCodeAsync(ClientSession session)
    {
        // 1. Проверка чёрного списка
        if (Blacklist.Contains(session.Ip))
        {
            Log.Error($"Клиент #{session.Id} ({session.Ip}) находится в чёрном списке — в лицензии отказано");
            await DisconnectAsync(session,
                Notice(MessageTypes.Blocked, $"Ваш IP-адрес {session.Ip} находится в чёрном списке сервера"),
                "отклонён: IP в чёрном списке");
            return;
        }

        // Повторный запрос в той же сессии — возвращаем уже выданный код
        if (session.Code is not null)
        {
            await session.SendAsync(BuildCodeMessage(session));
            return;
        }

        // 2. Проверка наличия свободного места и генерация уникального кода
        bool granted;
        lock (_licenseLock)
        {
            granted = UsedLicenses < Settings.MaxClients;
            if (granted)
                session.AssignCode(_codeGenerator.Next());
        }

        if (!granted)
        {
            Log.Error($"Клиент #{session.Id} ({session.Ip}): нет свободных лицензий " +
                      $"({Settings.MaxClients} из {Settings.MaxClients} заняты)");
            await DisconnectAsync(session,
                Notice(MessageTypes.NoSlots,
                    $"Нет свободных лицензий: заняты {Settings.MaxClients} из {Settings.MaxClients}. Повторите попытку позже."),
                "нет свободных лицензий");
            return;
        }

        Log.Success($"Клиенту #{session.Id} выдан код {session.Code}. " +
                    $"Занято лицензий: {UsedLicenses}/{Settings.MaxClients}");
        await session.SendAsync(BuildCodeMessage(session));
    }

    private async Task HandleConfirmAsync(ClientSession session, Message message)
    {
        if (session.Code is null)
        {
            await session.SendAsync(Notice(MessageTypes.Error, "Код ещё не выдан. Сначала отправьте GET_CODE."));
            return;
        }

        if (!string.Equals(message.Code?.Trim(), session.Code, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Клиент #{session.Id}: неверный код подтверждения «{message.Code}»");
            await session.SendAsync(Notice(MessageTypes.Error, "Неверный код подтверждения"));
            return;
        }

        if (!session.Confirmed)
        {
            session.MarkConfirmed();
            Log.Success($"Клиент #{session.Id} подтвердил код {session.Code}");
        }

        await session.SendAsync(new Message
        {
            Type = MessageTypes.Confirmed,
            Code = session.Code,
            Text = "Код подтверждён, лицензия активна",
            RemainingSeconds = session.GetRemainingSeconds(Settings.SessionTimeoutSeconds)
        });
    }

    private async Task HandleCheckAsync(ClientSession session, Message message)
    {
        if (Blacklist.Contains(session.Ip))
        {
            await DisconnectAsync(session,
                Notice(MessageTypes.Blocked, $"Ваш IP-адрес {session.Ip} находится в чёрном списке сервера"),
                "добавлен в чёрный список");
            return;
        }

        if (session.Code is null ||
            !string.Equals(message.Code?.Trim(), session.Code, StringComparison.OrdinalIgnoreCase))
        {
            await session.SendAsync(Notice(MessageTypes.Invalid, "Код недействителен"));
            return;
        }

        if (!session.Confirmed)
        {
            await session.SendAsync(Notice(MessageTypes.Error, "Код не подтверждён"));
            return;
        }

        await session.SendAsync(new Message
        {
            Type = MessageTypes.Ok,
            Code = session.Code,
            Text = "Лицензия действительна",
            RemainingSeconds = session.GetRemainingSeconds(Settings.SessionTimeoutSeconds)
        });
    }

    /// <summary>
    /// Раз в секунду проверяет тайм-ауты:
    /// не запрошен/не подтверждён код за ConfirmTimeout, истекло время сессии (SessionTimeout).
    /// </summary>
    private async Task MonitorLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var now = DateTime.Now;
                foreach (var session in _sessions.Values)
                {
                    if (session.IsClosing)
                        continue;

                    if (session.Code is null)
                    {
                        if ((now - session.ConnectedAt).TotalSeconds >= Settings.ConfirmTimeoutSeconds)
                            await DisconnectAsync(session,
                                Notice(MessageTypes.Expired, "Код не был запрошен вовремя"),
                                "не запросил код вовремя");
                    }
                    else if (!session.Confirmed &&
                             (now - session.CodeIssuedAt!.Value).TotalSeconds >= Settings.ConfirmTimeoutSeconds)
                    {
                        await DisconnectAsync(session,
                            Notice(MessageTypes.Expired,
                                $"Код не подтверждён за {Settings.ConfirmTimeoutSeconds} сек"),
                            "код не подтверждён вовремя");
                    }
                    else if ((now - session.CodeIssuedAt!.Value).TotalSeconds >= Settings.SessionTimeoutSeconds)
                    {
                        await DisconnectAsync(session,
                            Notice(MessageTypes.SessionExpired,
                                "Время сессии истекло. Переподключитесь для получения нового кода."),
                            "истекло время сессии (обновление кода)");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Сервер остановлен.
        }
    }

    /// <summary>
    /// Отключение по инициативе сервера: отправляет уведомление, закрывает передачу
    /// и даёт клиенту время закрыть соединение самому, затем закрывает сокет.
    /// </summary>
    private async Task DisconnectAsync(ClientSession session, Message notice, string reason)
    {
        if (!session.BeginClosing(reason))
            return;

        await session.SendAsync(notice);
        session.ShutdownSend();
        _ = Task.Delay(CloseGracePeriod).ContinueWith(_ => session.Close());
    }

    private Message BuildCodeMessage(ClientSession session) => new()
    {
        Type = MessageTypes.Code,
        Code = session.Code,
        Text = "Уникальный код выдан. Подтвердите его командой CONFIRM.",
        ClientIp = session.Ip,
        ClientPort = session.Port,
        ConnectedAt = session.ConnectedAt,
        SessionSeconds = Settings.SessionTimeoutSeconds,
        ConfirmSeconds = Settings.ConfirmTimeoutSeconds,
        RemainingSeconds = session.GetRemainingSeconds(Settings.SessionTimeoutSeconds)
    };

    private static Message Notice(string type, string text) => new() { Type = type, Text = text };
}

/// <summary>Результат добавления в чёрный список.</summary>
public sealed record BanResult(string? Ip, bool Added, int Disconnected);
