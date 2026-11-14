using System.Text.RegularExpressions;

namespace Alexandra.Infrastructure;

public sealed record SessionLogHeaderField(string Label, string Value);
public sealed record SessionLogHeader(string Title, IReadOnlyList<SessionLogHeaderField> Fields, string Body);

/// <summary>Display-only cleanup for both new journals and older Markdown exports.</summary>
public static partial class SessionLogPresentation
{
    // Frontier journal symbols and display names: EDCD/FDevIDs shipyard.csv.
    // https://github.com/EDCD/FDevIDs/blob/master/shipyard.csv
    // Prefer an already-localised journal name; never guess the identity of a new/unknown symbol.
    private static readonly Dictionary<string, string> Ships = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SideWinder"] = "Sidewinder", ["Eagle"] = "Eagle", ["Hauler"] = "Hauler", ["Adder"] = "Adder",
        ["Viper"] = "Viper MkIII", ["CobraMkIII"] = "Cobra MkIII", ["Type6"] = "Type-6 Transporter",
        ["Dolphin"] = "Dolphin", ["Type7"] = "Type-7 Transporter", ["Asp"] = "Asp Explorer",
        ["Vulture"] = "Vulture", ["Empire_Trader"] = "Imperial Clipper", ["Federation_Dropship"] = "Federal Dropship",
        ["Orca"] = "Orca", ["Type9"] = "Type-9 Heavy", ["Python"] = "Python", ["BelugaLiner"] = "Beluga Liner",
        ["FerDeLance"] = "Fer-de-Lance", ["Anaconda"] = "Anaconda", ["Federation_Corvette"] = "Federal Corvette",
        ["Cutter"] = "Imperial Cutter", ["DiamondBack"] = "Diamondback Scout", ["Empire_Courier"] = "Imperial Courier",
        ["DiamondBackXL"] = "Diamondback Explorer", ["Empire_Eagle"] = "Imperial Eagle",
        ["Federation_Dropship_MkII"] = "Federal Assault Ship", ["Federation_Gunship"] = "Federal Gunship",
        ["Viper_MkIV"] = "Viper MkIV", ["CobraMkIV"] = "Cobra MkIV", ["Independant_Trader"] = "Keelback",
        ["Asp_Scout"] = "Asp Scout", ["Type9_Military"] = "Type-10 Defender", ["Krait_MkII"] = "Krait MkII",
        ["TypeX"] = "Alliance Chieftain", ["TypeX_2"] = "Alliance Crusader", ["TypeX_3"] = "Alliance Challenger",
        ["Krait_Light"] = "Krait Phantom", ["Mamba"] = "Mamba", ["Python_NX"] = "Python MkII",
        ["Type8"] = "Type-8 Transporter", ["Mandalay"] = "Mandalay", ["CobraMkV"] = "Cobra MkV",
        ["Corsair"] = "Corsair", ["PantherMkII"] = "Panther Clipper MkII", ["LakonMiner"] = "Type-11 Prospector",
        ["Explorer_NX"] = "Caspian Explorer", ["SmallCombat01_NX"] = "Kestrel Mk II",
        ["MediumTransport01"] = "Lynx Highliner", ["AnacondaMkII"] = "Anaconda Mk II"
    };

    public static string ShipName(string ship) => Ships.GetValueOrDefault(ship.Trim().Replace(@"\_", "_"), ship);
    internal static IEnumerable<string> KnownShipNames => Ships.Values;

    /// <summary>Extract only a generated journal's metadata. Unknown Markdown stays ordinary Markdown.</summary>
    public static SessionLogHeader? ReadHeader(string markdown)
    {
        markdown = Format(markdown);
        if (!markdown.StartsWith("# Gameplay session — ", StringComparison.Ordinal)) return null;
        var summary = SummaryHeading().Match(markdown);
        if (!summary.Success) return null;
        var lines = markdown[..summary.Index].Split('\n');
        var fields = new List<SessionLogHeaderField>();
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            // Do not silently consume handwritten paragraphs, lists, headings or malformed fields.
            if (!line.StartsWith("- ", StringComparison.Ordinal) || colon <= 2) return null;
            var value = line[(colon + 2)..].Trim();
            if (value.Length == 0) return null;
            fields.Add(new(line[2..colon], value));
        }
        return fields.Count == 0 ? null : new(lines[0][2..].TrimEnd('\r'), fields, markdown[summary.Index..]);
    }

    public static string Format(string markdown)
    {
        // Only our generated metadata header, never quoted player text, captions or arbitrary Markdown.
        if (!markdown.StartsWith("# Gameplay session — ", StringComparison.Ordinal)) return markdown;
        var summary = markdown.IndexOf("\n## Summary", StringComparison.Ordinal);
        if (summary < 0) return markdown;
        var header = markdown[..summary];
        header = ShipLine().Replace(header, match => "- Ship: " + ShipName(match.Groups[1].Value));
        header = SpanLine().Replace(header, "$1");
        return header + markdown[summary..];
    }

    [GeneratedRegex(@"(?m)^- Ship: ([^\r\n]+)")]
    private static partial Regex ShipLine();
    [GeneratedRegex(@"(?m)^(- Recorded span: [^\r\n]+?) \(not an active-play timer\)(?=\r?$)")]
    private static partial Regex SpanLine();
    [GeneratedRegex(@"(?m)^## Summary[ \t]*\r?$")]
    private static partial Regex SummaryHeading();
}
