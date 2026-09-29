using System.Security.Cryptography;

namespace PaperDotNet.Workspaces.Contracts;

/// <summary>
/// The roles of a workspace as principals (ADR-0035): visitors (<see cref="WorkspaceAccessLevel.Read"/>), members
/// (<see cref="WorkspaceAccessLevel.Contribute"/>) and owners (<see cref="WorkspaceAccessLevel.Manage"/>). Permission
/// entries name a role like a user or a group, so people who join the workspace later get access without new entries.
/// </summary>
public static class WorkspaceRolePrincipals
{
    /// <summary>The principal id of a role: derived from the workspace id, the same on every server.</summary>
    public static Guid Id(Guid workspaceId, WorkspaceAccessLevel role)
    {
        if (role is not (WorkspaceAccessLevel.Read or WorkspaceAccessLevel.Contribute or WorkspaceAccessLevel.Manage))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "A workspace role is Read, Contribute or Manage.");
        }

        Span<byte> input = stackalloc byte[17];
        workspaceId.TryWriteBytes(input, bigEndian: true, out _);
        input[16] = (byte)role;
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // RFC 9562 version 8 (custom)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(hash[..16], bigEndian: true);
    }
}
