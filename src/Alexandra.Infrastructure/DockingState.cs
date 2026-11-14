using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

internal static class DockingState
{
    public static DockingContext? Apply(DockingContext? current, JsonElement e)
    {
        var kind = MemoryProgress.Text(e, "event");
        var at = MemoryProgress.Time(e);
        if (at == DateTimeOffset.MinValue) return current;
        if (kind is "Commander" or "LoadGame" or "Shutdown" or "Died" or "Resurrect" or "Undocked" or
            "FSDJump" or "CarrierJump" or "SupercruiseEntry" or "SupercruiseExit" or "ShipyardSwap" or "ShipyardBuy") return null;
        if (kind is not ("Location" or "Docked" or "DockingGranted" or "DockingRequested" or "DockingCancelled" or "DockingDenied" or "DockingTimeout")) return current;
        if (kind == "Location" && CombatState.Bool(e, "Docked") != true) return null;
        var station = CombatState.Text(e, "StationName");
        if (string.IsNullOrWhiteSpace(station)) return null;
        var same = string.Equals(station, current?.Station, StringComparison.OrdinalIgnoreCase);
        var type = CombatState.Text(e, "StationType") ?? (same ? current?.StationType : null);
        int? pad = null;
        if (kind == "DockingGranted" && e.TryGetProperty("LandingPad", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var number) && number is >= 1 and <= 64) pad = number;
        if (kind == "Docked" && same) pad = current?.Pad;
        return new(station, type, pad, at, kind == "DockingGranted" && pad is not null, kind is "Location" or "Docked");
    }
}
