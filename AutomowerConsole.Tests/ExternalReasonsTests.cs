using AutomowerConsole.Core;

namespace AutomowerConsole.Tests;

[TestFixture]
public class ExternalReasonsTests
{
    [Test]
    public void NullCodeReturnsNull()
    {
        Assert.That(ExternalReasons.Describe(null), Is.Null);
    }

    [TestCase(6000, "Rain Guard")]
    [TestCase(6001, "Frost Guard")]
    [TestCase(6500, "Wildlife")]
    public void KnownSmartRoutineCodesMentionTheRoutineByName(int code, string expectedSubstring)
    {
        var label = ExternalReasons.Describe(code);
        Assert.That(label, Does.Contain(expectedSubstring));
    }

    [Test]
    public void UnidentifiedCodeInTheSmartRoutineRangeIsStillLabeledAsASmartRoutine()
    {
        // 6000/6001/6500 are the only ones with a named guess (per
        // aioautomower); anything else in 6000-6999 is a real, observed-
        // possible smart routine code we just haven't identified yet -
        // should say so, not silently fall through to the generic fallback.
        var label = ExternalReasons.Describe(6250);
        Assert.That(label, Does.Contain("Smart routine"));
    }

    [TestCase(1500, "Google Assistant")]
    [TestCase(2500, "Amazon Alexa")]
    [TestCase(3500, "Home Assistant")]
    [TestCase(5500, "Gardena")]
    public void KnownIntegrationRangesAreLabeled(int code, string expectedSubstring)
    {
        Assert.That(ExternalReasons.Describe(code), Does.Contain(expectedSubstring));
    }

    [Test]
    public void Code4002IsCalledOutSeparatelyFromGenericIfttt()
    {
        Assert.That(ExternalReasons.Describe(4002), Does.Contain("calendar"));
        Assert.That(ExternalReasons.Describe(4001), Is.EqualTo("IFTTT"));
    }

    [Test]
    public void UnknownCodeOutsideAnyRangeStillReturnsAUsefulLabel()
    {
        var label = ExternalReasons.Describe(42);
        Assert.That(label, Does.Contain("42"), "an unrecognized code should still surface the raw number rather than silently disappearing");
    }
}
