using FeatherQR.Internals;

namespace FeatherQR.Tests;

public class CharacterSetsUnitTest
{
    [Test]
    [Arguments('0', true)]
    [Arguments('1', true)]
    [Arguments('2', true)]
    [Arguments('3', true)]
    [Arguments('4', true)]
    [Arguments('5', true)]
    [Arguments('6', true)]
    [Arguments('7', true)]
    [Arguments('8', true)]
    [Arguments('9', true)]
    [Arguments('A', false)]
    [Arguments('#', false)]
    public async Task IsNumericTest(char c, bool expected)
    {
        var result = CharacterSets.IsNumeric(c);
        await Assert.That(result).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments('0', true)]
    [Arguments('9', true)]
    [Arguments('A', true)]
    [Arguments('Z', true)]
    [Arguments(' ', true)]
    [Arguments('$', true)]
    [Arguments('%', true)]
    [Arguments('*', true)]
    [Arguments('+', true)]
    [Arguments('-', true)]
    [Arguments('.', true)]
    [Arguments('/', true)]
    [Arguments(':', true)]
    [Arguments('a', false)] // small letters are invalid
    [Arguments('z', false)]
    [Arguments('@', false)]
    [Arguments('#', false)]
    public async Task IsAlphanumericTest(char c, bool expected)
    {
        var result = CharacterSets.IsAlphanumeric(c);
        await Assert.That(result).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments('0', 0)]
    [Arguments('9', 9)]
    [Arguments('A', 10)]
    [Arguments('Z', 35)]
    [Arguments(' ', 36)]
    [Arguments('$', 37)]
    [Arguments('%', 38)]
    [Arguments('*', 39)]
    [Arguments('+', 40)]
    [Arguments('-', 41)]
    [Arguments('.', 42)]
    [Arguments('/', 43)]
    [Arguments(':', 44)]
    [Arguments('a', -1)] // small letters are invalid
    [Arguments('z', -1)]
    [Arguments('@', -1)]
    [Arguments('#', -1)]
    public async Task GetAlphanumericValueTest(char c, int expected)
    {
        await Assert.That(CharacterSets.IsAlphanumeric(c)).IsEqualTo(expected != -1);
        if (expected == -1)
            await Assert.That(() => CharacterSets.GetAlphanumericValue(c)).ThrowsExactly<ArgumentException>();
        else
            await Assert.That(CharacterSets.GetAlphanumericValue(c)).IsEqualTo(expected);
    }
}
