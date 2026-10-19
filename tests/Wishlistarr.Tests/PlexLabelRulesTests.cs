using Wishlistarr.Core.Kids;

namespace Wishlistarr.Tests;

public sealed class PlexLabelRulesTests
{
    [Theory]
    [InlineData("Ada")]
    [InlineData("kids room")]
    [InlineData("Zoë & Co.")]
    public void Valid_labels_are_accepted(string label) => Assert.Null(PlexLabelRules.Validate(label));

    [Fact]
    public void Longest_label_is_accepted() => Assert.Null(PlexLabelRules.Validate(new string('a', PlexLabelRules.MaxLength)));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("a,b")]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    public void Invalid_labels_are_rejected(string label) => Assert.NotNull(PlexLabelRules.Validate(label));

    [Fact]
    public void Overlong_labels_are_rejected() =>
        Assert.NotNull(PlexLabelRules.Validate(new string('a', PlexLabelRules.MaxLength + 1)));
}
