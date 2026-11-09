using System.Net;

namespace LicenseServer;

/// <summary>Ввод значений с консоли с проверкой корректности.</summary>
public static class ConsoleInput
{
    public static int AskInt(string label, int defaultValue, int min, int max)
    {
        while (true)
        {
            Console.Write($"{label} [{defaultValue}]: ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return defaultValue;

            if (int.TryParse(input, out int value) && value >= min && value <= max)
                return value;

            Log.Error($"Введите целое число от {min} до {max}.");
        }
    }

    public static IPAddress AskIp(string label, string defaultValue)
    {
        while (true)
        {
            Console.Write($"{label} [{defaultValue}]: ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                input = defaultValue;

            if (IPAddress.TryParse(input, out var address) && input.Contains('.'))
                return address;

            Log.Error("Некорректный IPv4-адрес. Пример: 127.0.0.1");
        }
    }
}
