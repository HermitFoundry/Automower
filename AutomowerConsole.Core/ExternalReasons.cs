// Source: reverse-engineered ID ranges from the aioautomower project
// (https://github.com/Thomas55555/aioautomower, model_planner.py's
// resolve_external_reason) - not documented anywhere by Husqvarna itself,
// so treat these as best-guess labels, not ground truth. Only meaningful
// when PlannerInfo.RestrictedReason is "EXTERNAL".
//
// Specifically uncertain: code 6000 is labeled "Rain Guard" by aioautomower,
// but the user has pointed out Husqvarna actually ships two distinct
// weather-driven smart routines - Rain Guard (skips based on forecast) and
// Weather Timer (adjusts total daily mowing time based on real-time grass
// growth). Every occurrence of an "EXTERNAL" restriction observed on this
// account's real data so far has carried code 6000 (confirmed via the raw
// track-*.jsonl logs, 2026-07-27) - it's not yet known whether that's really
// only Rain Guard triggering, or whether Husqvarna's firmware reports both
// routines under one shared code. Only a case where the Husqvarna app
// explicitly attributes a skip to "Weather Timer" specifically, cross-
// referenced against that moment's own logged code, can resolve this.
namespace AutomowerConsole.Core;

public static class ExternalReasons
{
    public static string? Describe(int? code) => code switch
    {
        null => null,
        >= 1000 and <= 1999 => "Google Assistant",
        >= 2000 and <= 2999 => "Amazon Alexa",
        >= 3000 and <= 3999 => "Home Assistant",
        4002 => "IFTTT calendar connection",
        >= 4000 and <= 4999 => "IFTTT",
        >= 5000 and <= 5999 => "Gardena Smart System",
        6000 => "Smart routine (Rain Guard? - unconfirmed, may also cover Weather Timer)",
        6001 => "Smart routine (Frost Guard)",
        6500 => "Smart routine (Wildlife protection)",
        >= 6000 and <= 6999 => "Smart routine (unidentified)",
        >= 100000 and <= 199999 => "IFTTT applet",
        >= 200000 and <= 299999 => "Developer portal",
        _ => $"External restriction (code {code})",
    };
}
