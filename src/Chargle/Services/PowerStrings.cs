namespace Chargle.Services;

/// <summary>
/// The one place power state is turned into English. The main window and the heads-up indicator
/// both describe the same thing, and they should never disagree about how to say it.
/// </summary>
public static class PowerStrings
{
    public static string Headline(PowerSource source) => source switch
    {
        PowerSource.Ac => "Plugged in",
        PowerSource.Battery => "On battery",
        PowerSource.Ups => "On backup power",
        _ => "Power state unknown",
    };

    public static string MilestoneHeadline(BatteryMilestone milestone) => milestone switch
    {
        BatteryMilestone.Full => "Battery charged",
        _ => "Battery low",
    };

    public static string MilestoneDetail(BatteryMilestone milestone, int percent)
    {
        if (percent < 0) return "";

        return milestone == BatteryMilestone.Full
            ? $"Reached {percent}%"
            : $"{percent}% remaining";
    }

    public static string Detail(PowerState state)
    {
        bool hasBattery = state.BatteryPercent >= 0;

        return (state.Source, hasBattery) switch
        {
            (PowerSource.Ac, true) when state.BatteryPercent >= 100 => "Battery full",
            (PowerSource.Ac, true) => $"Charging, {state.BatteryPercent}%",
            (PowerSource.Ac, false) => "This PC has no battery",
            (_, true) => $"{state.BatteryPercent}% remaining",
            _ => "",
        };
    }
}
