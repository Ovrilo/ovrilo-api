using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Communities.Contracts;
using Ovrilo.Modules.Communities.Domain;
using Ovrilo.Modules.Communities.Persistence;
using Ovrilo.Modules.Identity.Contracts;

namespace Ovrilo.Modules.Communities.Features;

internal static class InvitationEndpoints
{
    private const int CodeLength = 12;
    private const int DefaultLifetimeHours = 24;
    private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static async Task<Results<Created<InvitationResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid communityId,
        CreateInvitationRequest request,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var membership = await db.FindActiveMembershipAsync(communityId, userId, cancellationToken);
        if (membership is null)
        {
            return CommunityProblems.NotFound();
        }

        if (!membership.Value.Member.Permissions.HasFlag(CommunityPermissions.CreateInvites))
        {
            return CommunityProblems.PermissionDenied();
        }

        var maxUses = request.MaxUses ?? 1;
        var lifetimeHours = request.ExpiresInHours ?? DefaultLifetimeHours;
        var errors = new Dictionary<string, string[]>();
        if (maxUses is < 1 or > Invitation.MaxUsesLimit)
        {
            errors["maxUses"] = [$"Max uses must be 1-{Invitation.MaxUsesLimit}."];
        }

        if (lifetimeHours is < 1 or > Invitation.MaxLifetimeHours)
        {
            errors["expiresInHours"] = [$"Expiry must be 1-{Invitation.MaxLifetimeHours} hours."];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var code = RandomNumberGenerator.GetString(CodeAlphabet, CodeLength);
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(now),
            CommunityId = communityId,
            CreatedBy = userId,
            CodeHash = HashCode(code),
            MaxUses = maxUses,
            CreatedAt = now,
            ExpiresAt = now.AddHours(lifetimeHours),
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/communities/{communityId}/invitations/{invitation.Id}",
            new InvitationResponse(invitation.Id, communityId, code, maxUses, invitation.ExpiresAt));
    }

    /// <summary>Revokes an invitation. Its creator or a member with <see cref="CommunityPermissions.ManageInvites"/> may revoke. Idempotent.</summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> RevokeAsync(
        Guid communityId,
        Guid invitationId,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetRequiredUserId();
        var membership = await db.FindActiveMembershipAsync(communityId, userId, cancellationToken);
        if (membership is null)
        {
            return CommunityProblems.NotFound();
        }

        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.CommunityId == communityId, cancellationToken);
        if (invitation is null)
        {
            return CommunityProblems.InvitationNotFound();
        }

        if (invitation.CreatedBy != userId && !membership.Value.Member.Permissions.HasFlag(CommunityPermissions.ManageInvites))
        {
            return CommunityProblems.PermissionDenied();
        }

        invitation.RevokedAt ??= timeProvider.GetDatabaseUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Shows where a usable invitation leads without using it. Same answers as accepting for unusable codes.</summary>
    public static async Task<Results<Ok<InvitationPreviewResponse>, ProblemHttpResult>> PreviewAsync(
        PreviewInvitationRequest request,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var code = (request.Code ?? string.Empty).Trim();
        if (code.Length != CodeLength)
        {
            return CommunityProblems.InvitationInvalid();
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var codeHash = HashCode(code);
        var invitation = await db.Invitations.AsNoTracking()
            .SingleOrDefaultAsync(
                i => i.CodeHash == codeHash
                    && i.RevokedAt == null
                    && i.ExpiresAt > now
                    && (i.MaxUses == null || i.UseCount < i.MaxUses),
                cancellationToken);
        var community = invitation is null
            ? null
            : await db.Communities.AsNoTracking().SingleOrDefaultAsync(c => c.Id == invitation.CommunityId && c.DeletedAt == null, cancellationToken);
        if (invitation is null || community is null)
        {
            return CommunityProblems.InvitationInvalid();
        }

        var userId = principal.GetRequiredUserId();
        var state = await db.Members.AsNoTracking()
            .Where(m => m.CommunityId == community.Id && m.UserId == userId)
            .Select(m => (MembershipState?)m.State)
            .SingleOrDefaultAsync(cancellationToken);
        if (state == MembershipState.Banned)
        {
            return CommunityProblems.Banned();
        }

        var memberCount = await db.Members.CountAsync(m => m.CommunityId == community.Id && m.State == MembershipState.Active, cancellationToken);
        return TypedResults.Ok(new InvitationPreviewResponse(
            community.Id,
            community.Name,
            community.IconAssetId,
            memberCount,
            state == MembershipState.Active,
            invitation.ExpiresAt));
    }

    /// <summary>Joins the invitation's community. Accepting while already a member succeeds without consuming a use.</summary>
    public static async Task<Results<Ok<CommunityResponse>, ProblemHttpResult>> AcceptAsync(
        AcceptInvitationRequest request,
        ClaimsPrincipal principal,
        CommunitiesDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var code = (request.Code ?? string.Empty).Trim();
        if (code.Length != CodeLength)
        {
            return CommunityProblems.InvitationInvalid();
        }

        var userId = principal.GetRequiredUserId();
        var codeHash = HashCode(code);
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(i => i.CodeHash == codeHash, cancellationToken);
        var community = invitation is null
            ? null
            : await db.Communities.AsNoTracking().SingleOrDefaultAsync(c => c.Id == invitation.CommunityId && c.DeletedAt == null, cancellationToken);
        if (invitation is null || community is null)
        {
            return CommunityProblems.InvitationInvalid();
        }

        var member = await db.Members.SingleOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == userId, cancellationToken);
        switch (member?.State)
        {
            case MembershipState.Banned:
                return CommunityProblems.Banned();
            case MembershipState.Active:
                return TypedResults.Ok(CommunityEndpoints.ToResponse(community, member.Role));
        }

        var now = timeProvider.GetDatabaseUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Atomic: concurrent accepts cannot exceed MaxUses, and expiry/revocation are rechecked in the same statement.
        var consumed = await db.Invitations
            .Where(i => i.Id == invitation.Id
                && i.RevokedAt == null
                && i.ExpiresAt > now
                && (i.MaxUses == null || i.UseCount < i.MaxUses))
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.UseCount, i => i.UseCount + 1), cancellationToken);
        if (consumed == 0)
        {
            return CommunityProblems.InvitationInvalid();
        }

        if (member is null)
        {
            member = new CommunityMember { Id = Guid.CreateVersion7(now), CommunityId = community.Id, UserId = userId };
            db.Members.Add(member);
        }

        member.Role = CommunityRole.Member;
        member.State = MembershipState.Active;
        member.JoinedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // The same user accepted concurrently; that request joined them. Roll back this use.
            await transaction.RollbackAsync(cancellationToken);
        }

        return TypedResults.Ok(CommunityEndpoints.ToResponse(community, CommunityRole.Member));
    }

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
