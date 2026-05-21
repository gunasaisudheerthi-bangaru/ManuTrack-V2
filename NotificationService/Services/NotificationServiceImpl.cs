using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NotificationService.Enums;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Responses;
using NotificationService.DTOs;
using NotificationService.Models;
using NotificationService.Repositories.Interfaces;
using NotificationService.Services.Interfaces;

namespace NotificationService.Services;

public class NotificationServiceImpl(
    INotificationRepository repo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<NotificationServiceImpl> logger) : INotificationService
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? GetBearerToken()
    {
        var auth = httpContextAccessor.HttpContext?.Request.Headers["Authorization"].ToString();
        return auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? auth["Bearer ".Length..] : null;
    }

    private (int UserId, string UserName) GetCurrentUser()
    {
        var user = httpContextAccessor.HttpContext?.User;
        var idVal = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? user?.FindFirst("sub")?.Value;
        var name = user?.FindFirst(ClaimTypes.Name)?.Value
                ?? user?.FindFirst("name")?.Value
                ?? "Unknown";
        int.TryParse(idVal, out var id);
        return (id, name);
    }

    // Change 3: audit log (fire-and-forget)
    private async Task LogAuditAsync(string action, string entityType, string entityId, string? details = null)
    {
        try
        {
            var (userId, userName) = GetCurrentUser();
            if (userId == 0) return;

            var client = httpClientFactory.CreateClient("ComplianceService");
            var token = GetBearerToken();
            if (token != null)
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            await client.PostAsJsonAsync("api/v1/audit", new
            {
                UserID = userId,
                UserName = userName,
                Action = action,
                EntityType = entityType,
                EntityID = entityId,
                ServiceName = "NotificationService",
                Details = details
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in NotificationService."); }
    }

    // Change 6: auto-calculate expiry based on category
    private static DateTime? GetExpiryDate(string category) => category switch
    {
        NotificationCategory.WorkOrder  => DateTime.UtcNow.AddDays(7),
        NotificationCategory.General    => DateTime.UtcNow.AddDays(30),
        _                               => null   // Inventory, Quality, Compliance never expire
    };

    // ── Operations ────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<NotificationViewModel>>> GetForUserAsync(
        int userId, string? status, string? category)
    {
        var items = await repo.GetByUserIdAsync(userId, status, category);
        return ApiResponse<IEnumerable<NotificationViewModel>>.Ok(items.Select(Map));
    }

    public async Task<ApiResponse<IEnumerable<NotificationViewModel>>> GetAllAsync(
        string? category, string? status)
    {
        var items = await repo.GetAllAsync(category, status);
        return ApiResponse<IEnumerable<NotificationViewModel>>.Ok(items.Select(Map));
    }

    public async Task<ApiResponse<NotificationViewModel>> GetByIdAsync(int id)
    {
        var n = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Notification {id} not found.");
        return ApiResponse<NotificationViewModel>.Ok(Map(n));
    }

    // Change 2 + 3 + 5 + 6
    public async Task<ApiResponse<NotificationViewModel>> SendAsync(SendNotificationRequest request)
    {
        var notification = new Notification
        {
            UserID = request.UserID,
            Title = request.Title,
            Message = request.Message,
            Category = request.Category,
            Priority = request.Priority,
            Status = NotificationStatus.Unread,
            ExpiryDate = GetExpiryDate(request.Category),  // Change 6
            CreatedDate = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(notification);

        // Change 3: audit log
        await LogAuditAsync("Sent Notification", "Notification", created.NotificationID.ToString(),
            $"To UserID: {request.UserID}, Category: {request.Category}, Priority: {request.Priority}");

        return ApiResponse<NotificationViewModel>.Ok(Map(created), "Notification sent.");
    }

    // Change 3 + 5 + 6
    public async Task<ApiResponse<IEnumerable<NotificationViewModel>>> BroadcastAsync(
        BroadcastNotificationRequest request)
    {
        var expiry = GetExpiryDate(request.Category);

        var notifications = request.UserIDs.Select(uid => new Notification
        {
            UserID = uid,
            Title = request.Title,
            Message = request.Message,
            Category = request.Category,
            Priority = request.Priority,
            Status = NotificationStatus.Unread,
            ExpiryDate = expiry,        // Change 6
            CreatedDate = DateTime.UtcNow
        });

        var created = await repo.CreateBulkAsync(notifications);

        // Change 3: audit log
        await LogAuditAsync("Broadcast Notification", "Notification",
            string.Join(",", request.UserIDs.Take(10)),
            $"Category: {request.Category}, Priority: {request.Priority}, " +
            $"Recipients: {request.UserIDs.Count}");

        return ApiResponse<IEnumerable<NotificationViewModel>>.Ok(
            created.Select(Map),
            $"Broadcast sent to {request.UserIDs.Count} users.");
    }

    public async Task<ApiResponse<NotificationViewModel>> MarkAsReadAsync(int id)
    {
        var n = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Notification {id} not found.");
        n.Status = NotificationStatus.Read;
        n.ReadDate = DateTime.UtcNow;
        n.UpdatedDate = DateTime.UtcNow;
        var updated = await repo.UpdateAsync(n);
        return ApiResponse<NotificationViewModel>.Ok(Map(updated), "Notification marked as read.");
    }

    public async Task<ApiResponse> MarkAllAsReadAsync(int userId)
    {
        var items = await repo.GetByUserIdAsync(userId, NotificationStatus.Unread);
        foreach (var n in items)
        {
            n.Status = NotificationStatus.Read;
            n.ReadDate = DateTime.UtcNow;
            n.UpdatedDate = DateTime.UtcNow;
            await repo.UpdateAsync(n);
        }
        return ApiResponse.Ok("All notifications marked as read.");
    }

    // Change 4: breakdown by category
    public async Task<ApiResponse<UnreadCountViewModel>> GetUnreadCountAsync(int userId)
    {
        var byCat = await repo.GetUnreadCountByCategoryAsync(userId);

        var vm = new UnreadCountViewModel
        {
            Total = byCat.Values.Sum(),
            ByCategory = new Dictionary<string, int>
            {
                [NotificationCategory.WorkOrder]  = byCat.GetValueOrDefault(NotificationCategory.WorkOrder),
                [NotificationCategory.Inventory]  = byCat.GetValueOrDefault(NotificationCategory.Inventory),
                [NotificationCategory.Quality]    = byCat.GetValueOrDefault(NotificationCategory.Quality),
                [NotificationCategory.Compliance] = byCat.GetValueOrDefault(NotificationCategory.Compliance),
                [NotificationCategory.General]    = byCat.GetValueOrDefault(NotificationCategory.General)
            }
        };

        return ApiResponse<UnreadCountViewModel>.Ok(vm);
    }

    // Change 7: delete read notifications for current user
    public async Task<ApiResponse> DeleteReadNotificationsAsync(int userId)
    {
        var count = await repo.DeleteReadByUserAsync(userId);
        return ApiResponse.Ok($"{count} read notification(s) deleted successfully.");
    }

    // Change 7: admin cleanup — delete notifications older than 90 days
    public async Task<ApiResponse> CleanupOldNotificationsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var count = await repo.DeleteOlderThanAsync(cutoff);

        await LogAuditAsync("Cleanup Old Notifications", "Notification", "bulk",
            $"Deleted {count} notifications older than {cutoff:yyyy-MM-dd}");

        return ApiResponse.Ok($"{count} old notification(s) cleaned up successfully.");
    }

    // ── Mapper ────────────────────────────────────────────────────────────────

    private static NotificationViewModel Map(Notification n) => new()
    {
        NotificationID = n.NotificationID,
        UserID         = n.UserID,
        Title          = n.Title,
        Message        = n.Message,
        Category       = n.Category,
        Status         = n.Status,
        Priority       = n.Priority,
        ExpiryDate     = n.ExpiryDate,
        CreatedDate    = n.CreatedDate,
        UpdatedDate    = n.UpdatedDate,
        ReadDate       = n.ReadDate
    };
}
