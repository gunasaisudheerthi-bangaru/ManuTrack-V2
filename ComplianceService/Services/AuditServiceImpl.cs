using ComplianceService.DTOs;
using ComplianceService.Models;
using ComplianceService.Repositories.Interfaces;
using ComplianceService.Services.Interfaces;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Responses;

namespace ComplianceService.Services;

public class AuditServiceImpl(IAuditRepository repo) : IAuditService
{
    private const int MaxPageSize = 20;

    public async Task<ApiResponse<PagedAuditViewModel>> GetAllAsync(
        string? userId,
        string? serviceName,
        DateTime? from,
        DateTime? to,
        string? entityType,
        string? action,
        string? entityId,
        int page,
        int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var total = await repo.CountAsync(userId, serviceName, from, to, entityType, action, entityId);
        var entries = await repo.GetAllAsync(userId, serviceName, from, to, entityType, action, entityId, page, pageSize);

        return ApiResponse<PagedAuditViewModel>.Ok(new PagedAuditViewModel
        {
            Data = entries.Select(Map),
            Pagination = new AuditPaginationViewModel
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalRecords = total,
                TotalPages = (int)Math.Ceiling((double)total / pageSize)
            }
        });
    }

    public async Task<ApiResponse<AuditEntryViewModel>> GetByIdAsync(int id)
    {
        var entry = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Audit entry {id} not found.");
        return ApiResponse<AuditEntryViewModel>.Ok(Map(entry));
    }

    public async Task<ApiResponse<AuditEntryViewModel>> LogAsync(LogAuditEntryRequest request)
    {
        var entry = new AuditEntry
        {
            UserID = request.UserID,
            UserName = request.UserName,
            Action = request.Action,
            EntityType = request.EntityType,
            EntityID = request.EntityID,
            ServiceName = request.ServiceName,
            Details = request.Details,
            Timestamp = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(entry);
        return ApiResponse<AuditEntryViewModel>.Ok(Map(created), "Audit entry logged.");
    }

    private static AuditEntryViewModel Map(AuditEntry a) => new()
    {
        AuditID = a.AuditID,
        UserID = a.UserID,
        UserName = a.UserName,
        Action = a.Action,
        EntityType = a.EntityType,
        EntityID = a.EntityID,
        ServiceName = a.ServiceName,
        Details = a.Details,
        Timestamp = a.Timestamp
    };
}
