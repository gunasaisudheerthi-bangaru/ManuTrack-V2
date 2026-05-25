using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using QualityService.Enums;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Helpers;
using ManuTrack.SharedKernel.Responses;
using QualityService.DTOs;
using QualityService.Models;
using QualityService.Repositories.Interfaces;
using QualityService.Services.Interfaces;

namespace QualityService.Services;

public class DefectServiceImpl(
    IDefectRepository defectRepo,
    IInspectionRepository inspectionRepo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<DefectServiceImpl> logger) : IDefectService
{
    // ── Change 1: Audit logging (fire-and-forget) ─────────────────────────────
    private async Task LogAuditAsync(string action, string entityType, string entityId, string? details = null)
    {
        try
        {
            var (userId, userName) = ServiceHelper.GetCurrentUser(httpContextAccessor);
            if (userId == 0) return;

            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "ComplianceService");
            await client.PostAsJsonAsync("api/v1/audit", new
            {
                UserID = userId,
                UserName = userName,
                Action = action,
                EntityType = entityType,
                EntityID = entityId,
                ServiceName = "QualityService",
                Details = details
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in DefectService."); }
    }

    // ── Change 4: Critical defect — notify + cancel work order (fire-and-forget) ──
    private async Task HandleCriticalDefectAsync(int defectId, int workOrderId)
    {
        // 4a: Send notification
        try
        {
            var (userId, _) = ServiceHelper.GetCurrentUser(httpContextAccessor);
            if (userId != 0)
            {
                var notifClient = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "NotificationService");
                await notifClient.PostAsJsonAsync("api/v1/notifications", new
                {
                    UserID = userId,
                    Title = "Critical Defect Detected",
                    Message = $"A Critical defect (ID #{defectId}) has been logged for Work Order #{workOrderId}. " +
                              "The work order has been automatically cancelled for review.",
                    Category = "Quality"
                });
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Critical defect notification failed for defect {DefectId}.", defectId); }

        // 4b: Cancel the work order
        try
        {
            var woClient = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "WorkOrderService");
            await woClient.PutAsJsonAsync($"api/v1/workorders/{workOrderId}/status", new
            {
                Status = "Cancelled"
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Work order cancellation failed for WO {WorkOrderId} after critical defect.", workOrderId); }
    }

    // ── Operations ────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<DefectViewModel>>> GetByInspectionIdAsync(int inspectionId)
    {
        if (!await inspectionRepo.ExistsAsync(inspectionId))
            throw new NotFoundException($"Inspection {inspectionId} not found.");

        var defects = await defectRepo.GetByInspectionIdAsync(inspectionId);
        return ApiResponse<IEnumerable<DefectViewModel>>.Ok(defects.Select(Map));
    }

    public async Task<ApiResponse<IEnumerable<DefectViewModel>>> GetAllAsync(string? status, string? severity)
    {
        var defects = await defectRepo.GetAllAsync(status, severity);
        return ApiResponse<IEnumerable<DefectViewModel>>.Ok(defects.Select(Map));
    }

    public async Task<ApiResponse<DefectViewModel>> GetByIdAsync(int id)
    {
        var defect = await defectRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Defect {id} not found.");
        return ApiResponse<DefectViewModel>.Ok(Map(defect));
    }

    public async Task<ApiResponse<DefectViewModel>> CreateAsync(CreateDefectRequest request)
    {
        var inspection = await inspectionRepo.GetByIdWithDefectsAsync(request.InspectionID)
            ?? throw new NotFoundException($"Inspection {request.InspectionID} not found.");

        var defect = new Defect
        {
            InspectionID = request.InspectionID,
            Description = request.Description,
            Severity = request.Severity,
            Status = DefectStatus.Open,
            CreatedDate = DateTime.UtcNow
        };

        var created = await defectRepo.CreateAsync(defect);

        await LogAuditAsync("Created Defect", "Defect", created.DefectID.ToString(),
            $"InspectionID: {created.InspectionID}, Severity: {created.Severity}");

        // Change 4: if Critical, notify and auto-cancel the work order
        if (request.Severity == "Critical")
            await HandleCriticalDefectAsync(created.DefectID, inspection.WorkOrderID);

        return ApiResponse<DefectViewModel>.Ok(Map(created), "Defect logged.");
    }

    public async Task<ApiResponse<DefectViewModel>> ResolveAsync(int id, ResolveDefectRequest request)
    {
        var defect = await defectRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Defect {id} not found.");

        // Change 2: explicit service-level validation
        if (string.IsNullOrWhiteSpace(request.ResolutionDescription))
            throw new ValidationException("Resolution description is required to resolve a defect.");

        defect.ResolutionDescription = request.ResolutionDescription;
        defect.Status = DefectStatus.Resolved;
        defect.ResolvedDate = DateTime.UtcNow;
        defect.UpdatedDate = DateTime.UtcNow;

        var updated = await defectRepo.UpdateAsync(defect);

        await LogAuditAsync("Resolved Defect", "Defect", id.ToString(),
            $"Resolution: {request.ResolutionDescription[..Math.Min(50, request.ResolutionDescription.Length)]}");

        return ApiResponse<DefectViewModel>.Ok(Map(updated), "Defect resolved.");
    }

    public async Task<ApiResponse<DefectViewModel>> UpdateStatusAsync(int id, UpdateDefectStatusRequest request)
    {
        var defect = await defectRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Defect {id} not found.");

        defect.Status = request.Status;
        defect.UpdatedDate = DateTime.UtcNow;

        var updated = await defectRepo.UpdateAsync(defect);

        await LogAuditAsync("Updated Defect Status", "Defect", id.ToString(),
            $"New Status: {request.Status}");

        return ApiResponse<DefectViewModel>.Ok(Map(updated), "Defect status updated.");
    }

    // ── Mapper ────────────────────────────────────────────────────────────────

    private static DefectViewModel Map(Defect d) => new()
    {
        DefectID = d.DefectID,
        InspectionID = d.InspectionID,
        Description = d.Description,
        Severity = d.Severity,
        Status = d.Status,
        ResolutionDescription = d.ResolutionDescription,
        CreatedDate = d.CreatedDate,
        UpdatedDate = d.UpdatedDate,
        ResolvedDate = d.ResolvedDate
    };
}
