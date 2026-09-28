using StatusBar.Core.Common;

namespace StatusBar.Core.Tests.Common;

public class QuestionDetectorTests
{
    [Theory]
    [InlineData("Is that correct?", true)]
    [InlineData("Is that correct?**”)", true)]
    [InlineData("第一行？", true)]
    [InlineData("Code: `var ready = true?`", true)]
    [InlineData("I drafted the report.\n\nWhich format would you like?\n\n1. PDF\n2. Word", true)]
    [InlineData("Two options:\n\nShould I use A or B?\n- A: faster\n- B: simpler", true)]
    [InlineData("Should I continue with the second section? Let me know.", true)]
    [InlineData("Should I continue?\nLet me know and I will carry on.", true)]
    [InlineData("Could you confirm the client name?\r\n\r\n(a) Venta\r\n(b) Other", true)]
    [InlineData("Done. The summary is in outputs/report.md.", false)]
    [InlineData("Was this hard? Yes.\n\nAll tests pass and the file is saved.", false)]
    [InlineData("Results:\n1. Passed\n2. Passed", false)]
    [InlineData("", false)]
    [InlineData("   \n\t", false)]
    public void Detects_a_question_in_the_final_paragraph(string text, bool expected)
    {
        Assert.Equal(expected, QuestionDetector.EndsWithQuestion(text));
    }

    [Fact]
    public void Null_is_not_a_question()
    {
        Assert.False(QuestionDetector.EndsWithQuestion(null));
    }
}
