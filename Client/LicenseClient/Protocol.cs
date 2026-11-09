using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LicenseClient;

/// <summary>Типы сообщений протокола (см. README.md в корне репозитория).</summary>
public static class MessageTypes
{
    // Клиент -> сервер
    public const string GetCode = "GET_CODE";
    public const string Confirm = "CONFIRM";
    public const string Check = "CHECK";
    public const string Bye = "BYE";

    // Сервер -> клиент
    public const string Code = "CODE";
    public const string Confirmed = "CONFIRMED";
    public const string Ok = "OK";
    public const string NoSlots = "NO_SLOTS";
    public const string Blocked = "BLOCKED";
    public const string Expired = "EXPIRED";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string Kicked = "KICKED";
    public const string Invalid = "INVALID";
    public const string Error = "ERROR";
}

/// <summary>Сообщение протокола. Передаётся как одна строка JSON.</summary>
public sealed class Message
{
    public string Type { get; set; } = "";
    public string? Code { get; set; }
    public string? Text { get; set; }
    public string? ClientIp { get; set; }
    public int? ClientPort { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public int? SessionSeconds { get; set; }
    public int? ConfirmSeconds { get; set; }
    public int? RemainingSeconds { get; set; }
}

public static class Protocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        // Кириллица передаётся как есть, без экранирования
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(Message message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Разбирает строку JSON. Возвращает null, если строка некорректна.</summary>
    public static Message? Deserialize(string line)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Message>(line, Options);
            return string.IsNullOrWhiteSpace(message?.Type) ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
