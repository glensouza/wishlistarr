namespace Wishlistarr.Core.Content;

public interface IContentRule
{
    ContentDecision Evaluate(ContentTitle title, ContentPolicy policy);
}
