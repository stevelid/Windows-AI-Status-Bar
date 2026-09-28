using StatusBar.Core.Common;

namespace StatusBar.Core.Tests.Common;

public class QuestionDetectorTests
{
    [Theory]
    [InlineData("Is that correct?", true)]
    [InlineData("Is that correct?**”)", true)]
    [InlineData("第一行？", true)]
    [InlineData("Question?\nA final statement.", false)]
    [InlineData("Code: `var ready = true?`", true)]
    [InlineData("", false)]
    [InlineData("   \n\t", false)]
    public void Checks_only_the_final_nonempty_line(string text, bool expected)
    {
        Assert.Equal(expected, QuestionDetector.EndsWithQuestion(text));
    }

    [Fact]
    public void Null_is_not_a_question()
    {
        Assert.False(QuestionDetector.EndsWithQuestion(null));
    }
}
