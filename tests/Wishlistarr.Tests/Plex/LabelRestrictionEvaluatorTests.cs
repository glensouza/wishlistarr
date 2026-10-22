using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Tests.Plex;

public sealed class LabelRestrictionEvaluatorTests
{
    [Theory]
    [InlineData("label=Ada", LabelFilterMode.AllowOnly)]
    [InlineData("contentRating=TV-Y%2CTV-Y7|label=Ada", LabelFilterMode.AllowOnly)]
    [InlineData("label=Ben%2CAda", LabelFilterMode.AllowOnly)]
    [InlineData("label=ada", LabelFilterMode.AllowOnly)]
    [InlineData("label!=Ada", LabelFilterMode.Exclude)]
    [InlineData("contentRating!=R|label!=Ben%2CAda", LabelFilterMode.Exclude)]
    public void Filters_naming_the_label_enforce_it(string filter, LabelFilterMode mode)
    {
        LabelRestriction result = LabelRestrictionEvaluator.Evaluate(filter, "Ada");

        Assert.Equal(LabelEnforcement.Enforced, result.State);
        Assert.Equal(mode, result.Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("label=Ben")]
    [InlineData("label!=Ben")]
    [InlineData("contentRating=TV-Y")]
    [InlineData("contentRating=Ada")]
    [InlineData("label=Adam")]
    public void Filters_without_the_label_do_not_enforce_it(string filter)
    {
        LabelRestriction result = LabelRestrictionEvaluator.Evaluate(filter, "Ada");

        Assert.Equal(LabelEnforcement.NotEnforced, result.State);
        Assert.Null(result.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("something unexpected")]
    [InlineData("<html>")]
    public void Missing_or_unreadable_filters_are_unknown(string? filter) =>
        Assert.Equal(LabelEnforcement.Unknown, LabelRestrictionEvaluator.Evaluate(filter, "Ada").State);

    [Fact]
    public void An_enforced_label_wins_over_an_unreadable_part()
    {
        LabelRestriction result = LabelRestrictionEvaluator.Evaluate("garbage|label=Ada", "Ada");

        Assert.Equal(LabelEnforcement.Enforced, result.State);
    }

    [Fact]
    public void Labels_with_spaces_are_matched_after_decoding()
    {
        LabelRestriction result = LabelRestrictionEvaluator.Evaluate("label=Little%20Ada", "Little Ada");

        Assert.Equal(LabelEnforcement.Enforced, result.State);
    }

    [Fact]
    public void The_all_types_filter_counts_for_movies_and_television()
    {
        ProfileRestrictions result = LabelRestrictionEvaluator.Evaluate(new UserContentFilters("label=Ada", string.Empty, string.Empty), "Ada");

        Assert.Equal(LabelEnforcement.Enforced, result.Movies.State);
        Assert.Equal(LabelEnforcement.Enforced, result.Television.State);
    }

    [Fact]
    public void Movies_and_television_are_judged_separately()
    {
        ProfileRestrictions result = LabelRestrictionEvaluator.Evaluate(new UserContentFilters(string.Empty, "label=Ada", "label=Ben"), "Ada");

        Assert.Equal(LabelEnforcement.Enforced, result.Movies.State);
        Assert.Equal(LabelEnforcement.NotEnforced, result.Television.State);
    }

    [Fact]
    public void A_missing_type_filter_stays_unknown_when_the_all_filter_does_not_help()
    {
        ProfileRestrictions result = LabelRestrictionEvaluator.Evaluate(new UserContentFilters(string.Empty, null, string.Empty), "Ada");

        Assert.Equal(LabelEnforcement.Unknown, result.Movies.State);
        Assert.Equal(LabelEnforcement.NotEnforced, result.Television.State);
    }
}
