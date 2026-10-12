using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary><see cref="Gs1Error"/>: the default is no error, equality is by value, and the message names the kind and the place.</summary>
public class Gs1ErrorTest
{
    [Test]
    public async Task Default_IsNone()
    {
        var error = default(Gs1Error);
        await Assert.That(error.Kind).IsEqualTo(Gs1ErrorKind.None);
        await Assert.That((error.Offset, error.Length)).IsEqualTo((0, 0));
        await Assert.That(error.ToString()).IsEqualTo("No error.");
    }

    [Test]
    public async Task Create_AndEquality()
    {
        var a = new Gs1Error(Gs1ErrorKind.InvalidCheckDigit, 15, 1);
        var b = new Gs1Error(Gs1ErrorKind.InvalidCheckDigit, 15, 1);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a == b).IsTrue();
        await Assert.That(a.Equals(new Gs1Error(Gs1ErrorKind.InvalidDate, 15, 1))).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    [Arguments(Gs1ErrorKind.InvalidCheckDigit, 15, 1, "InvalidCheckDigit at 15 (1 character): the check digit is wrong.")]
    [Arguments(Gs1ErrorKind.MissingRequiredAi, 0, 5, "MissingRequiredAi at 0 (5 characters): a required AI is missing.")]
    [Arguments(Gs1ErrorKind.DestinationTooSmall, 0, 0, "DestinationTooSmall: the destination is too small.")]
    public async Task ToString_NamesTheKindAndThePlace(Gs1ErrorKind kind, int offset, int length, string message)
    {
        await Assert.That(new Gs1Error(kind, offset, length).ToString()).IsEqualTo(message);
    }

    [Test]
    public async Task EveryKind_HasAMessage()
    {
        foreach (var kind in Enum.GetValues<Gs1ErrorKind>())
        {
            var message = new Gs1Error(kind, 0, 0).ToString();
            await Assert.That(message).IsNotEmpty().Because(kind.ToString());
            await Assert.That(message).EndsWith(".").Because(kind.ToString());
        }
    }

    [Test]
    public async Task Create_RejectsNegativePositions()
    {
        await Assert.That(() => new Gs1Error(Gs1ErrorKind.UnknownAi, -1, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new Gs1Error(Gs1ErrorKind.UnknownAi, 0, -1)).Throws<ArgumentOutOfRangeException>();
    }
}
