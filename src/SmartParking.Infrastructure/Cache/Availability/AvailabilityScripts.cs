namespace SmartParking.Infrastructure.Cache.Availability;

/// <summary>Lua scripts of the availability view, embedded in the assembly (<c>Lua.*.lua</c>).</summary>
internal static class AvailabilityScripts
{
    public static readonly string ApplyOccupancyEvent = Load("Lua.ApplyOccupancyEvent.lua");

    private static string Load(string name)
    {
        using var stream = typeof(AvailabilityScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded script {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
