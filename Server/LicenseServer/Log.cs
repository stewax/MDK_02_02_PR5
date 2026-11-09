namespace LicenseServer;

/// <summary>Потокобезопасный вывод сообщений в консоль с отметкой времени.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static void Info(string text) => Write(text, ConsoleColor.Gray);
    public static void Success(string text) => Write(text, ConsoleColor.Green);
    public static void Warn(string text) => Write(text, ConsoleColor.Yellow);
    public static void Error(string text) => Write(text, ConsoleColor.Red);

    /// <summary>Выполняет вывод блока текста без перемешивания с другими потоками.</summary>
    public static void Block(Action write)
    {
        lock (Sync)
            write();
    }

    private static void Write(string text, ConsoleColor color)
    {
        lock (Sync)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Console.ForegroundColor = previous;
        }
    }
}
