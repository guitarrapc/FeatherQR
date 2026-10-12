using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;
using QRInteropFixtures;

namespace FeatherQR.Tests;

/// <summary>
/// AIs the pinned dictionary does not assign, which the tests use as their "unknown AI" examples. Each sits under a
/// prefix Table 7-5 knows, so a reader splits it, unlike (89), whose prefix has no length at all and stays unknown
/// whatever the dictionary says. A dictionary release can assign any of them, and <see cref="Gs1DictionaryTest"/>
/// says so in one place when it does, so the update picks another example once instead of hunting through the tests.
/// </summary>
public static class Gs1UnassignedAi
{
    /// <summary>Four digits under prefix 70, read to the next separator.</summary>
    public const string Ai7050 = "7050";

    /// <summary>Three digits under prefix 23, read to the next separator.</summary>
    public const string Ai234 = "234";

    /// <summary>Four digits under prefix 31, read by the predefined length of six.</summary>
    public const string Ai3106 = "3106";

    /// <summary>Three digits under prefix 41, read by the predefined length of thirteen.</summary>
    public const string Ai418 = "418";

    public static readonly string[] All = [Ai7050, Ai234, Ai3106, Ai418];
}

/// <summary>What the library says about its dictionary, against the dictionary file the table was generated from.</summary>
public class Gs1DictionaryTest
{
    [Test]
    public async Task DictionaryRelease_IsTheReleaseOfThePinnedFile()
    {
        var dictionary = new Gs1SyntaxDictionary(Gs1FixtureLoader.DictionaryPath);
        await Assert.That(Gs1ElementString.DictionaryRelease).IsEqualTo(dictionary.Release);
        await Assert.That(Gs1ElementString.DictionaryRelease).Matches(@"^\d{4}-\d{2}-\d{2}$");
    }

    [Test]
    public async Task EveryUnassignedExample_IsUnassignedUnderAKnownPrefix()
    {
        var dictionary = new Gs1SyntaxDictionary(Gs1FixtureLoader.DictionaryPath);
        foreach (var ai in Gs1UnassignedAi.All)
        {
            await Assert.That(dictionary.TryGet(ai, out _)).IsFalse().Because($"({ai}) is assigned in dictionary {dictionary.Release}, so the tests need another unassigned example");
            await Assert.That(Gs1AiTable.TryFind(ai, out _)).IsFalse().Because(ai);
            await Assert.That(Gs1AiTable.AiLength(ai[0], ai[1])).IsEqualTo(ai.Length).Because(ai);
        }
    }
}
