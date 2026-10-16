namespace FeatherQR.Tests;

/// <summary>
/// The construction side of <see cref="Fnc1Mode"/>: <see cref="Fnc1Mode.None"/>
/// is <c>default</c>, <see cref="Fnc1Mode.FirstPosition"/> is GS1, and second position takes a two-digit number or
/// one letter, each form refusing what ISO/IEC 18004 6.4.8.2 does not define, so no contradiction and no invalid
/// indicator can be expressed, and a decoded mode goes back into the options unchanged.
/// </summary>
public class Fnc1ModeTest
{
    [Test]
    public async Task None_IsTheDefault()
    {
        await Assert.That(Fnc1Mode.None).IsEqualTo(default(Fnc1Mode));
        await Assert.That(Fnc1Mode.None.IsFirstPosition).IsFalse();
        await Assert.That(Fnc1Mode.None.IsSecondPosition).IsFalse();
        await Assert.That(Fnc1Mode.None.ApplicationIndicator).IsNull();
        await Assert.That(new QRCodeGeneratorOptions().Fnc1).IsEqualTo(Fnc1Mode.None);
    }

    [Test]
    public async Task FirstPosition_IsGs1()
    {
        var mode = Fnc1Mode.FirstPosition;
        await Assert.That(mode.IsFirstPosition).IsTrue();
        await Assert.That(mode.IsSecondPosition).IsFalse();
        await Assert.That(mode.ApplicationIndicator).IsNull();
        await Assert.That(mode).IsNotEqualTo(Fnc1Mode.None);
    }

    [Test]
    [Arguments(0)]
    [Arguments(37)]
    [Arguments(99)]
    public async Task SecondPosition_Number_IsTheIndicatorAsItsValue(int number)
    {
        var mode = Fnc1Mode.SecondPosition(number);
        await Assert.That(mode.IsSecondPosition).IsTrue();
        await Assert.That(mode.IsFirstPosition).IsFalse();
        await Assert.That(mode.ApplicationIndicator).IsEqualTo(number);
    }

    [Test]
    [Arguments('A', 165)]
    [Arguments('Z', 190)]
    [Arguments('a', 197)]
    [Arguments('z', 222)]
    public async Task SecondPosition_Letter_IsTheIndicatorAsItsAsciiValuePlus100(char letter, int codeword)
    {
        var mode = Fnc1Mode.SecondPosition(letter);
        await Assert.That(mode.IsSecondPosition).IsTrue();
        await Assert.That(mode.ApplicationIndicator).IsEqualTo(codeword);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(100)]
    [Arguments(165)]
    [Arguments(int.MaxValue)]
    public async Task SecondPosition_NumberOutside00To99_Throws(int number)
    {
        await Assert.That(() => Fnc1Mode.SecondPosition(number)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments('0')]
    [Arguments('!')]
    [Arguments(' ')]
    [Arguments('é')]
    [Arguments('\u001D')]
    [Arguments('@')]
    [Arguments('[')]
    [Arguments('`')]
    [Arguments('{')]
    public async Task SecondPosition_CharacterThatIsNotALetter_Throws(char letter)
    {
        await Assert.That(() => Fnc1Mode.SecondPosition(letter)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Equality_IsByModeAndIndicator()
    {
        await Assert.That(Fnc1Mode.SecondPosition(37)).IsEqualTo(Fnc1Mode.SecondPosition(37));
        await Assert.That(Fnc1Mode.SecondPosition('A')).IsEqualTo(Fnc1Mode.SecondPosition('A'));
        await Assert.That(Fnc1Mode.SecondPosition(37)).IsNotEqualTo(Fnc1Mode.SecondPosition(38));
        await Assert.That(Fnc1Mode.SecondPosition(0)).IsNotEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(Fnc1Mode.SecondPosition(0)).IsNotEqualTo(Fnc1Mode.None);
    }

    /// <summary>
    /// A letter indicator prints the letter it stands for beside its codeword. A number prints as itself, 99 too, the last below
    /// the letters. A mode with no indicator prints none, as its <see cref="Fnc1Mode.ApplicationIndicator"/> is null.
    /// </summary>
    [Test]
    public async Task ToString_LetterIndicator_ShowsTheLetter()
    {
        await Assert.That(Fnc1Mode.SecondPosition('A').ToString()).Contains("ApplicationIndicator = 165 (A) }");
        await Assert.That(Fnc1Mode.SecondPosition('z').ToString()).Contains("ApplicationIndicator = 222 (z) }");
        await Assert.That(Fnc1Mode.SecondPosition(37).ToString()).Contains("ApplicationIndicator = 37 }");
        await Assert.That(Fnc1Mode.SecondPosition(99).ToString()).Contains("ApplicationIndicator = 99 }");
        await Assert.That(Fnc1Mode.None.ToString()).Contains("ApplicationIndicator =  }");
        await Assert.That(Fnc1Mode.FirstPosition.ToString()).Contains("IsFirstPosition = True, IsSecondPosition = False, ApplicationIndicator =  }");
    }

    /// <summary>A decoded mode is the same value the encoder takes, so a symbol can be re-encoded from its decode info.</summary>
    [Test]
    public async Task ADecodedMode_GoesBackIntoTheOptionsUnchanged()
    {
        foreach (var mode in new[] { Fnc1Mode.FirstPosition, Fnc1Mode.SecondPosition(37), Fnc1Mode.SecondPosition('z') })
        {
            var data = QRCodeGenerator.Create("AB12", QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = mode });
            await Assert.That(QRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
            await Assert.That(info.Fnc1).IsEqualTo(mode);
            var again = QRCodeGenerator.Create("AB12", QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = info.Fnc1 });
            await Assert.That(again.GetRawData()).IsEquivalentTo(data.GetRawData(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    /// <summary>The option and the segmentation are independent: either can be set without disturbing the other.</summary>
    [Test]
    public async Task Fnc1AndSegmentation_AreIndependentOptions()
    {
        var options = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal, Fnc1 = Fnc1Mode.SecondPosition('z') };
        await Assert.That(options.Segmentation).IsEqualTo(QRSegmentation.Optimal);
        await Assert.That(options.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition('z'));
        options.Segmentation = QRSegmentation.Single;
        await Assert.That(options.Fnc1).IsEqualTo(Fnc1Mode.SecondPosition('z'));
        options.Fnc1 = Fnc1Mode.None;
        await Assert.That(options.Segmentation).IsEqualTo(QRSegmentation.Single);
        await Assert.That(options).IsEqualTo(default(QRCodeGeneratorOptions));
        await Assert.That(new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition }).IsEqualTo(default(QRCodeGeneratorOptions) with { Fnc1 = Fnc1Mode.FirstPosition });
    }
}
