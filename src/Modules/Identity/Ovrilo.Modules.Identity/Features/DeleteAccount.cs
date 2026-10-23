using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Ovrilo.BuildingBlocks;
using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Identity.Contracts;
using Ovrilo.Modules.Identity.Domain;
using Ovrilo.Modules.Identity.Persistence;
using Ovrilo.Modules.Media.Contracts;

namespace Ovrilo.Modules.Identity.Features;

/// <summary>Deletes the caller's account; the password confirms it is really them.</summary>
public sealed record DeleteAccountRequest(string Password);

internal static class DeleteAccount
{
    public const string DeletedDisplayName = "Deleted user";

    /// <summary>
    /// Deletes the account: other modules may refuse first (for example a community owner), then
    /// the profile is scrubbed (username and email freed, picture removed), every session revoked,
    /// and each module removes what it keeps about the user. Messages stay as from "Deleted user".
    /// </summary>
    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> HandleAsync(
        DeleteAccountRequest request,
        ClaimsPrincipal principal,
        IdentityDbContext db,
        IPasswordHasher<User> passwordHasher,
        IMediaModule media,
        IEnumerable<IAccountDeletionParticipant> participants,
        TimeProvider timeProvider,
        ILogger<DeleteAccountRequest> logger,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["password"] = ["The password is not correct."] });
        }

        var modules = participants.ToList();
        foreach (var participant in modules)
        {
            if (await participant.CheckAsync(userId, cancellationToken) is { } reason)
            {
                return ApiProblems.Create(StatusCodes.Status409Conflict, "identity.account_delete_blocked", reason);
            }
        }

        var now = timeProvider.GetDatabaseUtcNow();
        var avatar = user.AvatarAssetId;
        // Placeholders no one can register ('-' is not allowed in usernames) free the real ones.
        var tombstone = ("-" + userId.ToString("N")).Substring(0, User.UsernameMaxLength);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(
                set => set
                    .SetProperty(u => u.Status, UserStatus.Deleted)
                    .SetProperty(u => u.Username, tombstone)
                    .SetProperty(u => u.Email, $"{userId:N}@deleted.invalid")
                    .SetProperty(u => u.DisplayName, DeletedDisplayName)
                    .SetProperty(u => u.PasswordHash, string.Empty)
                    .SetProperty(u => u.AvatarAssetId, (Guid?)null)
                    .SetProperty(u => u.DeletedAt, now)
                    .SetProperty(u => u.UpdatedAt, now),
                cancellationToken);
            await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.RevokedAt, now)
                    .SetProperty(s => s.RevocationReason, RevocationReasons.AccountDeleted),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (avatar is { } assetId)
        {
            await media.ReleaseAsync(assetId, cancellationToken);
        }

        foreach (var participant in modules)
        {
            try
            {
                await participant.DeleteAsync(userId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The account is gone either way; the leftover data is unreachable without it.
                DeleteAccountLog.CleanupFailed(logger, participant.GetType().Name, userId, ex);
            }
        }

        return TypedResults.NoContent();
    }
}

internal static partial class DeleteAccountLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "{Participant} could not remove data of deleted account {UserId}")]
    public static partial void CleanupFailed(ILogger logger, string participant, Guid userId, Exception exception);
}
