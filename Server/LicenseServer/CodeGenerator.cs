using System.Security.Cryptography;
using System.Text;

namespace LicenseServer;

/// <summary>
/// Генерирует уникальные коды вида XXXX-XXXX-XXXX.
/// Используется криптографически стойкий генератор; коды не повторяются за время работы сервера.
/// </summary>
public sealed class CodeGenerator
{
    // Без похожих символов (0/O, 1/I), чтобы код было удобно читать.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly HashSet<string> _issued = new();
    private readonly object _sync = new();

    public string Next()
    {
        lock (_sync)
        {
            string code;
            do
            {
                code = Generate();
            } while (!_issued.Add(code));

            return code;
        }
    }

    private static string Generate()
    {
        var sb = new StringBuilder(14);
        for (int i = 0; i < 12; i++)
        {
            if (i > 0 && i % 4 == 0)
                sb.Append('-');
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }
}
