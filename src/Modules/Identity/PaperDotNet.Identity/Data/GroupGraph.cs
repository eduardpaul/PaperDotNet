namespace PaperDotNet.Identity.Data;

/// <summary>Groups inside groups (ADR-0035): the closure of the nesting graph and the rules for changing it.</summary>
internal static class GroupGraph
{
    /// <summary>A chain of groups inside each other is at most this long.</summary>
    public const int MaxDepth = 10;

    /// <summary>Every (group, group it is inside of) pair, including (group, itself).</summary>
    public static HashSet<(Guid Group, Guid Ancestor)> Closure(IEnumerable<Guid> groups, IEnumerable<(Guid Group, Guid Member)> nestings)
    {
        var parents = nestings.ToLookup(n => n.Member, n => n.Group);
        var closure = new HashSet<(Guid, Guid)>();
        foreach (var group in groups)
        {
            foreach (var ancestor in Reachable(group, parents))
            {
                closure.Add((group, ancestor));
            }
        }

        return closure;
    }

    /// <summary>Why <paramref name="member"/> cannot go into <paramref name="group"/>, or null when it can.</summary>
    public static string? CheckNesting(Guid group, Guid member, IReadOnlyCollection<(Guid Group, Guid Member)> nestings)
    {
        if (group == member)
        {
            return "A group cannot contain itself.";
        }

        var parents = nestings.ToLookup(n => n.Member, n => n.Group);
        if (Reachable(group, parents).Contains(member))
        {
            return "The group is already inside that group; nesting it would make a cycle.";
        }

        var children = nestings.ToLookup(n => n.Group, n => n.Member);
        return Height(group, parents, []) + Height(member, children, []) > MaxDepth
            ? $"Groups can be nested at most {MaxDepth} levels deep."
            : null;
    }

    /// <summary>The group and every group reachable through <paramref name="next"/>; stops at cycles.</summary>
    private static HashSet<Guid> Reachable(Guid start, ILookup<Guid, Guid> next)
    {
        var seen = new HashSet<Guid>();
        var pending = new Stack<Guid>([start]);
        while (pending.TryPop(out var current))
        {
            if (seen.Add(current))
            {
                foreach (var other in next[current])
                {
                    pending.Push(other);
                }
            }
        }

        return seen;
    }

    /// <summary>The longest chain from <paramref name="group"/> through <paramref name="next"/>, counting the group.</summary>
    private static int Height(Guid group, ILookup<Guid, Guid> next, Dictionary<Guid, int> known)
    {
        if (known.TryGetValue(group, out var height))
        {
            return height;
        }

        known[group] = 1; // Guards against a cycle in stored data.
        height = 1 + next[group].Select(g => Height(g, next, known)).DefaultIfEmpty(0).Max();
        known[group] = height;
        return height;
    }
}
