using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using QualityService.Enums;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Responses;
using QualityService.DTOs;
using QualityService.Models;
using QualityService.Repositories.Interfaces;
using QualityService.Services.Interfaces;

namespace QualityService.Services;

public class InspectionServiceImpl(
    IInspectionRepository repo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<InspectionServiceImpl> logger) : IInspectionService
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

    private HttpClient CreateAuthorizedClient(string clientName)
    {
        var client = httpClientFactory.CreateClient(clientName);
        var token = GetBearerToken();
        if (token != null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── Change 1: Audit logging (fire-and-forget) ─────────────────────────────
    private async Task LogAuditAsync(string action, string entityType, string entityId, string? details = null)
    {
        try
        {
            var (userId, userName) = GetCurrentUser();
            if (userId == 0) return;

            var client = CreateAuthorizedClient("ComplianceService");
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
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in InspectionService."); }
    }

    // ── Change 3: Fail inspection notification (fire-and-forget) ──────────────
    private async Task NotifyInspectionFailedAsync(int inspectionId, int workOrderId)
    {
        try
        {
            var (userId, _) = GetCurrentUser();
            if (userId == 0) return;

            var client = CreateAuthorizedClient("NotificationService");
            await client.PostAsJsonAsync("api/v1/notifications", new
            {
                UserID = userId,
                Title = "Inspection Failed",
                Message = $"Inspection #{inspectionId} failed for Work Order #{workOrderId}. " +
                          "Please review defects reported.",
                Category = "Quality"
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed inspection notification failed for inspection {InspectionId}.", inspectionId); }
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<InspectionViewModel>>> GetAllAsync(string? status, int? workOrderId)
    {
        var items = await repo.GetAllAsync(status, workOrderId);
        return ApiResponse<IEnumerable<InspectionViewModel>>.Ok(items.Select(Map));
    }

    public async Task<ApiResponse<InspectionViewModel>> GetByIdAsync(int id)
    {
        var inspection = await repo.GetByIdWithDefectsAsync(id)
            ?? throw new NotFoundException($"Inspection {id} not found.");
        return ApiResponse<InspectionViewModel>.Ok(Map(inspection));
    }

    public async Task<ApiResponse<InspectionViewModel>> CreateAsync(CreateInspectionRequest request)
    {
        // Change 6: check WorkOrder status before creating inspection
        await ValidateWorkOrderStatusAsync(request.WorkOrderID);

        var inspection = new Inspection
        {
            WorkOrderID = request.WorkOrderID,
            InspectionDate = request.InspectionDate,
            InspectorID = request.InspectorID,
            InspectorName = request.InspectorName,
            Notes = request.Notes,
            Result = string.Empty,
            Status = InspectionStatus.Scheduled,
            CreatedDate = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(inspection);

        await LogAuditAsync("Created Inspection", "Inspection", created.InspectionID.ToString(),
            $"WorkOrderID: {created.WorkOrderID}, Inspector: {created.InspectorName}");

        return ApiResponse<InspectionViewModel>.Ok(Map(created), "Inspection created.");
    }

    public async Task<ApiResponse<InspectionViewModel>> UpdateResultAsync(int id, UpdateInspectionResultRequest request)
    {
        var inspection = await repo.GetByIdWithDefectsAsync(id)
            ?? throw new NotFoundException($"Inspection {id} not found.");

        inspection.Result = request.Result;
        inspection.Status = request.Status;
        if (request.Notes != null) inspection.Notes = request.Notes;
        inspection.UpdatedDate = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(inspection);

        await LogAuditAsync("Updated Inspection Result", "Inspection", id.ToString(),
            $"Result: {request.Result}, Status: {request.Status}");

        // Change 3: notify if result is Fail
        if (request.Result == InspectionResult.Fail)
            await NotifyInspectionFailedAsync(id, inspection.WorkOrderID);

        return ApiResponse<InspectionViewModel>.Ok(Map(updated), "Inspection result updated.");
    }

    // ── Change 6: Validate WorkOrder status before inspection ─────────────────
    private async Task ValidateWorkOrderStatusAsync(int workOrderId)
    {
        try
        {
            var client = CreateAuthorizedClient("WorkOrderService");
            var response = await client.GetAsync($"api/v1/workorders/{workOrderId}");
            if (!response.IsSuccessStatusCode) return; // if WO service down, allow through

            var result = await response.Content
                .ReadFromJsonAsync<WorkOrderResponseDto>(
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var status = result?.Data?.Status;
            if (status == null) return;

            if (status == "Cancelled")
                throw new ValidationException("Cannot create inspection for a cancelled work order.");

            if (status == "Pending" || status == "Scheduled")
                throw new ValidationException(
                    "Cannot create inspection for a work order that has not started yet.");
        }
        catch (ValidationException) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "WorkOrderService unavailable during status validation for WO {WorkOrderId}. Allowing through.", workOrderId); }
    }

    // ── Mapper ────────────────────────────────────────────────────────────────

    // Change 5: calculated defect breakdown
    private static InspectionViewModel Map(Inspection i) => new()
    {
        InspectionID = i.InspectionID,
        WorkOrderID = i.WorkOrderID,
        InspectionDate = i.InspectionDate,
        InspectorID = i.InspectorID,
        InspectorName = i.InspectorName,
        Result = i.Result,
        Status = i.Status,
        Notes = i.Notes,
        CreatedDate = i.CreatedDate,
        UpdatedDate = i.UpdatedDate,
        TotalDefectCount = i.Defects.Count,
        CriticalCount = i.Defects.Count(d => d.Severity == "Critical"),
        HighCount = i.Defects.Count(d => d.Severity == "High"),
        MediumCount = i.Defects.Count(d => d.Severity == "Medium"),
        LowCount = i.Defects.Count(d => d.Severity == "Low")
    };

    // ── Local DTOs for WorkOrderService response ──────────────────────────────
    private sealed class WorkOrderResponseDto
    {
        public WorkOrderDataDto? Data { get; set; }
    }

    private sealed class WorkOrderDataDto
    {
        public string Status { get; set; } = string.Empty;
    }
}
