namespace Alexandra.Core;

public sealed record DockingContext(string Station, string? StationType, int? Pad,
    DateTimeOffset ObservedAt, bool ClearanceActive, bool Docked);

public sealed record LandingPadAnswer(bool Located, int? Pad, string? Station, string? StationType,
    string Answer, string? Orientation = null);

public static class LandingPadGuide
{
    // Factual positions in the standard 45-pad starport. No third-party code/assets.
    // Viewed entering the slot, green navigation lights RIGHT. Source and limits:
    // docs/LANDING-PADS.md. Each row is a clock sector; groups are front/middle/rear.
    private static readonly (int Clock, int[] Front, int[] Middle, int[] Rear)[] Sectors = [
        (6, [1,2], [], [3,4]), (7, [5,6], [7], [8]), (8, [9], [], [10]), (9, [11,12], [13], [14,15]),
        (10, [16,17], [], [18,19]), (11, [20,21], [22], [23]), (12, [24], [], [25]), (1, [26,27], [28], [29,30]),
        (2, [31,32], [], [33,34]), (3, [35,36], [37], [38]), (4, [39], [], [40]), (5, [41,42], [43], [44,45])
    ];
    private static readonly HashSet<string> StandardTypes = new(["Coriolis", "Orbis", "Ocellus"], StringComparer.OrdinalIgnoreCase);

    public static LandingPadAnswer Describe(DockingContext? context, int? requestedPad, DateTimeOffset now)
    {
        if (requestedPad is < 1 or > 64)
            return new(false, requestedPad, context?.Station, context?.StationType, "That pad number is outside the supported range.");
        if (context is null || context.ObservedAt > now)
            return new(false, requestedPad, null, null, "I don't have your current station's docking details yet.");
        var assigned = context.Docked || context.ClearanceActive && now - context.ObservedAt <= TimeSpan.FromMinutes(10)
            ? context.Pad : null;
        var pad = requestedPad ?? assigned;
        if (pad is null)
            return new(false, null, context.Station, context.StationType, "I don't have a current assigned pad. Which pad number?");
        if (context.StationType is null || !StandardTypes.Contains(context.StationType) || pad > 45)
            return new(false, pad, context.Station, context.StationType,
                $"I don't have a mapped position for pad {pad} at this station. I can look at your docking display if you ask.");
        var sector = Sectors.Single(s => s.Front.Contains(pad.Value) || s.Middle.Contains(pad.Value) || s.Rear.Contains(pad.Value));
        var depth = sector.Front.Contains(pad.Value) ? "near the entrance" : sector.Middle.Contains(pad.Value) ? "halfway down the station" : "near the back";
        var side = sector.Clock switch { 12 => "on the upper side", 6 => "on the lower side", 3 => "on the right",
            9 => "on the left", 1 or 2 => "on the upper-right side", 4 or 5 => "on the lower-right side",
            7 or 8 => "on the lower-left side", _ => "on the upper-left side" };
        const string orientation = "Entering the slot with the green lights on your right";
        return new(true, pad, context.Station, context.StationType,
            $"Pad {pad} is {depth}, {side}, at {sector.Clock} o'clock. Green entry lights on your right.", orientation);
    }
}
