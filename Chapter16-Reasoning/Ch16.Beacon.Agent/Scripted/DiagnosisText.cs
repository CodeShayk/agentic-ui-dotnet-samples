using System.Globalization;

namespace Ch16.Beacon.Agent.Scripted;

/// <summary>What the scripted model thinks and says about WO-1001: each step's full chain, its summary and its answer.</summary>
public static class DiagnosisText
{
    /// <summary>The run capacitor's rating, and the reading below which it is replaced (6% under).</summary>
    private const decimal Rated = 45m;
    private const decimal Floor = 42.3m;

    public const string Chain1 =
        "The question is why WO-1001 is short-cycling. Short-cycling with warm air usually means the compressor " +
        "stops on its overload protector before the space cools. The overload trips when the compressor runs hot, " +
        "and three things commonly do that on a CA-36X: a weak run capacitor, high head pressure from a dirty " +
        "condenser coil, or a condenser fan that has slowed down. The fault history will separate them, so I'll " +
        "read it before recommending anything.";

    public const string Summary1 =
        "Short-cycling with warm air points to the compressor tripping on overload. Reading EQ-501's fault history " +
        "before naming a cause.";

    public const string Chain2 =
        "EQ-501 logged E3, high head pressure, in July 2025 and again in June 2026, then E5, a compressor overload " +
        "trip, on Saturday. Repeated high head pressure means the condenser isn't shedding heat, so a dirty coil or a " +
        "slow condenser fan comes first, and the heat has probably been wearing out the run capacitor as well. A run " +
        "capacitor more than 6% under its 45 µF rating, below 42.3 µF, gets replaced whatever else is wrong. The " +
        "compressor is the expensive answer at $1,795.00 and comes last. If the coil turns out to be clean, someone " +
        "should call Harbor View Dental on 555-0141 and ask whether anything has been stacked against the outdoor unit.";

    public const string Summary2 =
        "Two high-head-pressure faults and an overload trip point at heat rejection: a dirty condenser coil or a slow " +
        "condenser fan, which also wears out the run capacitor. The compressor comes last. If the coil is clean, call " +
        "Harbor View Dental (555-0141) about anything blocking the outdoor unit.";

    public const string Answer2 =
        "The likely cause is heat rejection, not the compressor: EQ-501 logged high head pressure twice before " +
        "Saturday's overload trip.\n\n" +
        "1. Clean the condenser coil and check that the condenser fan runs at full speed.\n" +
        "2. Measure the run capacitor. CAP-45-5 is rated 45/5 µF; replace it if it reads below 42.3 µF.\n" +
        "3. Measure compressor amperage only after that, and don't order a compressor yet.";

    /// <summary>The follow-up when the earlier chain came back: the answer builds on its threshold and its cause.</summary>
    public static (string Summary, string Chain, string Answer) WithEarlierDiagnosis(decimal reading)
    {
        var (value, under) = (Number(reading), Number(Math.Round((Rated - reading) / Rated * 100m, 1)));
        if (reading >= Floor)
        {
            return (
                $"{value} µF is within 6% of the 45 µF rating, so the capacitor stays. The coil is still the first suspect.",
                $"{value} µF is within 6% of the 45 µF rating, above the 42.3 µF floor from the diagnosis, so the capacitor " +
                "is healthy. The two E3 faults still say the condenser is running hot, so the coil and the condenser fan " +
                "come next, then compressor amperage.",
                $"Picking up from my earlier diagnosis: {value} µF is within tolerance, so keep the capacitor.\n\n" +
                "Have Priya clean the condenser coil and check the condenser fan next, then measure compressor amperage.");
        }

        return (
            $"{value} µF is below the 42.3 µF floor from the earlier diagnosis, so the capacitor goes. The repeated high " +
            "head pressure still needs fixing, or the new one will fail the same way.",
            $"{value} µF is {under}% under the 45 µF rating, well below the 42.3 µF floor from the diagnosis, so the " +
            "capacitor is finished. It failed because the condenser has been running hot, which the two E3 faults already " +
            "showed, so replacing it alone would only buy time. CAP-45-5 costs $38.50, under the $500 approval limit.",
            $"Picking up from my earlier diagnosis: {value} µF is {under}% under the 45 µF rating and below the 42.3 µF " +
            "floor, so replace CAP-45-5 ($38.50) today.\n\n" +
            "The capacitor is a symptom, though. Two high-head-pressure faults say the condenser has been running hot, so " +
            "have Priya clean the coil and check the condenser fan before she leaves, or the new capacitor will go the same way.");
    }

    /// <summary>The follow-up without the earlier chain: the reading alone, and an honest gap.</summary>
    public static (string Summary, string Chain, string Answer) WithoutEarlierDiagnosis(decimal reading)
    {
        var (value, under) = (Number(reading), Number(Math.Round((Rated - reading) / Rated * 100m, 1)));
        if (reading >= Floor)
        {
            return (
                $"{value} µF is within 6% of a 45 µF rating. There's no earlier diagnosis to go on.",
                $"A 45 µF run capacitor reading {value} µF is within tolerance. Nothing earlier in this conversation says " +
                "which unit this is or what else is wrong with it.",
                $"{value} µF is within 6% of a 45 µF rating, so the capacitor is fine.\n\n" +
                "I don't have my earlier reasoning about this unit, so tell me what else Priya has found and I'll look again.");
        }

        return (
            $"{value} µF against a 45 µF rating is {under}% low. There's no earlier diagnosis to say why it failed.",
            $"A 45 µF run capacitor reading {value} µF is {under}% low. Nothing earlier in this conversation says which " +
            "unit this is or why the capacitor failed.",
            $"{value} µF is {under}% under a 45 µF rating, so replace the capacitor (CAP-45-5 is $38.50).\n\n" +
            "I don't have my earlier reasoning about this unit, so I can't say why it failed. Tell me what else Priya " +
            "has found and I'll look again.");
    }

    private static string Number(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
