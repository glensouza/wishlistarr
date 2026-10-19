namespace Wishlistarr.Api.Security;

public sealed class SetupState
{
    private volatile bool isComplete;

    public bool IsComplete => this.isComplete;

    public void MarkComplete() => this.isComplete = true;
}
