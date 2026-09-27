namespace SmartClean.Core;

public static class Formatting
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "Not reported";
        double value = Math.Max(0, bytes.Value);
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
