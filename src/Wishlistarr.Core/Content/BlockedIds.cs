namespace Wishlistarr.Core.Content;

internal static class BlockedIds
{
    public static int? First(IReadOnlyList<int>? ids, IReadOnlySet<int> blocked)
    {
        if (ids is null || blocked.Count == 0)
        {
            return null;
        }

        foreach (int id in ids)
        {
            if (blocked.Contains(id))
            {
                return id;
            }
        }

        return null;
    }
}
