using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LicenseServer;

/// <summary>Состояние одного подключённого клиента.</summary>
public sealed class ClientSession
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly TcpClient _tcp;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _confirmed;
    private int _closing;
    private int _closed;

    public ClientSession(int id, TcpClient tcp)
    {
        Id = id;
        _tcp = tcp;

        var endPoint = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        var address = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        Ip = address.ToString();
        Port = endPoint.Port;
        ConnectedAt = DateTime.Now;

        Stream = tcp.GetStream();
        _writer = new StreamWriter(Stream, Utf8NoBom) { AutoFlush = true, NewLine = "\n" };
    }

    public int Id { get; }
    public string Ip { get; }
    public int Port { get; }
    public DateTime ConnectedAt { get; }
    public NetworkStream Stream { get; }

    public string? Code { get; private set; }
    public DateTime? CodeIssuedAt { get; private set; }
    public bool Confirmed => _confirmed;

    /// <summary>Причина отключения (для журнала сервера).</summary>
    public string? CloseReason { get; set; }

    public bool IsClosing => Volatile.Read(ref _closing) == 1;

    public TimeSpan Duration => DateTime.Now - ConnectedAt;

    public string Status =>
        IsClosing ? "отключается" :
        Code is null ? "ожидает запрос кода" :
        !Confirmed ? "ожидает подтверждения" :
        "лицензия активна";

    public void AssignCode(string code)
    {
        Code = code;
        CodeIssuedAt = DateTime.Now;
        _confirmed = false;
    }

    public void MarkConfirmed() => _confirmed = true;

    /// <summary>Сколько секунд осталось до принудительного отключения для обновления кода.</summary>
    public int GetRemainingSeconds(int sessionTimeoutSeconds)
    {
        if (CodeIssuedAt is null)
            return sessionTimeoutSeconds;
        double left = sessionTimeoutSeconds - (DateTime.Now - CodeIssuedAt.Value).TotalSeconds;
        return Math.Max(0, (int)Math.Ceiling(left));
    }

    /// <summary>Помечает сессию как закрывающуюся. Возвращает false, если это уже сделано.</summary>
    public bool BeginClosing(string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
            return false;
        CloseReason = reason;
        return true;
    }

    public async Task SendAsync(Message message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(Protocol.Serialize(message));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Клиент уже отключился — сообщение доставить невозможно.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Сообщает клиенту, что сервер больше не будет отправлять данные (корректное закрытие TCP).</summary>
    public void ShutdownSend()
    {
        try { _tcp.Client.Shutdown(SocketShutdown.Send); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
            return;
        try { _tcp.Close(); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }
}
