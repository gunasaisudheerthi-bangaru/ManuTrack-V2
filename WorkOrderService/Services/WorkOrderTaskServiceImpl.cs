using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using WorkOrderService.Enums;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Helpers;
using ManuTrack.SharedKernel.Responses;
using WorkOrderService.DTOs;
using WorkOrderService.Models;
using WorkOrderService.Repositories.Interfaces;
using WorkOrderService.Services.Interfaces;

namespace WorkOrderService.Services;

public class WorkOrderTaskServiceImpl(
    IWorkOrderTaskRepository taskRepo,
    IWorkOrderRepository workOrderRepo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<WorkOrderTaskServiceImpl> logger) : IWorkOrderTaskService
{
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
                ServiceName = "WorkOrderService",
                Details = details
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in WorkOrderTaskService."); }
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<WorkOrderTaskViewModel>>> GetByWorkOrderIdAsync(int workOrderId)
    {
        if (!await workOrderRepo.ExistsAsync(workOrderId))
            throw new NotFoundException($"WorkOrder {workOrderId} not found.");

        var tasks = await taskRepo.GetByWorkOrderIdAsync(workOrderId);
        return ApiResponse<IEnumerable<WorkOrderTaskViewModel>>.Ok(tasks.Select(Map));
    }

    public async Task<ApiResponse<WorkOrderTaskViewModel>> GetByIdAsync(int id)
    {
        var task = await taskRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Task {id} not found.");
        return ApiResponse<WorkOrderTaskViewModel>.Ok(Map(task));
    }

    public async Task<ApiResponse<WorkOrderTaskViewModel>> CreateAsync(CreateWorkOrderTaskRequest request)
    {
        if (!await workOrderRepo.ExistsAsync(request.WorkOrderID))
            throw new NotFoundException($"WorkOrder {request.WorkOrderID} not found.");

        var task = new WorkOrderTask
        {
            WorkOrderID = request.WorkOrderID,
            Description = request.Description,
            AssignedTo = request.AssignedTo,
            Notes = request.Notes,
            Status = WorkOrderTaskStatus.Pending,
            CreatedDate = DateTime.UtcNow
        };

        var created = await taskRepo.CreateAsync(task);

        await LogAuditAsync("Created Task", "WorkOrderTask", created.TaskID.ToString(),
            $"WorkOrderID: {created.WorkOrderID}, AssignedTo: {created.AssignedTo}");

        return ApiResponse<WorkOrderTaskViewModel>.Ok(Map(created), "Task created successfully.");
    }

    public async Task<ApiResponse<WorkOrderTaskViewModel>> UpdateAsync(int id, UpdateWorkOrderTaskRequest request)
    {
        var task = await taskRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Task {id} not found.");

        if (request.Description != null) task.Description = request.Description;
        if (request.AssignedTo != null) task.AssignedTo = request.AssignedTo;
        if (request.Notes != null) task.Notes = request.Notes;
        task.UpdatedDate = DateTime.UtcNow;

        var updated = await taskRepo.UpdateAsync(task);

        await LogAuditAsync("Updated Task", "WorkOrderTask", id.ToString(),
            $"AssignedTo: {updated.AssignedTo}, Description: {updated.Description}");

        return ApiResponse<WorkOrderTaskViewModel>.Ok(Map(updated), "Task updated successfully.");
    }

    public async Task<ApiResponse<WorkOrderTaskViewModel>> UpdateStatusAsync(int id, UpdateTaskStatusRequest request)
    {
        var task = await taskRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Task {id} not found.");

        task.Status = request.Status;
        if (request.Status == WorkOrderTaskStatus.Completed)
            task.CompletedDate = DateTime.UtcNow;
        task.UpdatedDate = DateTime.UtcNow;

        var updated = await taskRepo.UpdateAsync(task);

        await LogAuditAsync("Updated Task Status", "WorkOrderTask", id.ToString(),
            $"New Status: {request.Status}");

        return ApiResponse<WorkOrderTaskViewModel>.Ok(Map(updated), "Task status updated.");
    }

    public async Task<ApiResponse> DeleteAsync(int id)
    {
        var task = await taskRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Task {id} not found.");

        await taskRepo.DeleteAsync(task);

        await LogAuditAsync("Deleted Task", "WorkOrderTask", id.ToString(),
            $"WorkOrderID: {task.WorkOrderID}, AssignedTo: {task.AssignedTo}");

        return ApiResponse.Ok("Task deleted successfully.");
    }

    // ── Mapper ───────────────────────────────────────────────────────────────

    private static WorkOrderTaskViewModel Map(WorkOrderTask t) => new()
    {
        TaskID = t.TaskID,
        WorkOrderID = t.WorkOrderID,
        Description = t.Description,
        AssignedTo = t.AssignedTo,
        Status = t.Status,
        CompletedDate = t.CompletedDate,
        Notes = t.Notes,
        CreatedDate = t.CreatedDate,
        UpdatedDate = t.UpdatedDate
    };
}
