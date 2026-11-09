namespace LicenseServer;

public static class Format
{
    public static string Duration(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
