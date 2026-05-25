using System.Net.Http.Json;
using System.Text.Json;
using ComplianceService.DTOs;
using ComplianceService.Enums;
using ComplianceService.Models;
using ComplianceService.Repositories.Interfaces;
using ComplianceService.Services.Interfaces;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Helpers;
using ManuTrack.SharedKernel.Responses;
using Microsoft.AspNetCore.Http;

namespace ComplianceService.Services;

public class ComplianceReportServiceImpl(
    IComplianceReportRepository repo,
    IAuditRepository auditRepo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ComplianceReportServiceImpl> logger) : IComplianceReportService
{
    // ── Change 4: Direct audit log via repository (no HTTP needed) ────────────
    private async Task WriteAuditAsync(string action, string entityId, string? details = null)
    {
        try
        {
            var (userId, userName) = ServiceHelper.GetCurrentUser(httpContextAccessor);
            if (userId == 0) return;

            await auditRepo.CreateAsync(new AuditEntry
            {
                UserID = userId,
                UserName = userName,
                Action = action,
                EntityType = "ComplianceReport",
                EntityID = entityId,
                ServiceName = "ComplianceService",
                Details = details,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Direct audit write failed in ComplianceReportService."); }
    }

    // ── Change 3: Notification helpers (fire-and-forget) ─────────────────────
    private async Task NotifyInReviewAsync(int reportId, string title)
    {
        try
        {
            var (userId, _) = ServiceHelper.GetCurrentUser(httpContextAccessor);
            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "NotificationService");
            await client.PostAsJsonAsync("api/v1/notifications", new
            {
                UserID = userId,
                TargetRole = "Admin",
                Title = "Compliance Report Ready for Review",
                Message = $"Compliance Report #{reportId} - {title} is ready for review and approval.",
                Category = "Compliance"
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "InReview notification failed for report {ReportId}.", reportId); }
    }

    private async Task NotifyApprovedAsync(int reportId, string title, int generatedByUserId)
    {
        try
        {
            if (generatedByUserId == 0) return;
            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "NotificationService");
            await client.PostAsJsonAsync("api/v1/notifications", new
            {
                UserID = generatedByUserId,
                Title = "Compliance Report Approved",
                Message = $"Your compliance report #{reportId} - {title} has been approved.",
                Category = "Compliance"
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Approved notification failed for report {ReportId}.", reportId); }
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<ComplianceReportViewModel>>> GetAllAsync(
        string? status, string? reportType)
    {
        var reports = await repo.GetAllAsync(status, reportType);
        return ApiResponse<IEnumerable<ComplianceReportViewModel>>.Ok(reports.Select(Map));
    }

    public async Task<ApiResponse<ComplianceReportViewModel>> GetByIdAsync(int id)
    {
        var report = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Compliance report {id} not found.");
        return ApiResponse<ComplianceReportViewModel>.Ok(Map(report));
    }

    public async Task<ApiResponse<ComplianceReportViewModel>> CreateAsync(CreateComplianceReportRequest request)
    {
        var (userId, userName) = ServiceHelper.GetCurrentUser(httpContextAccessor);

        var report = new ComplianceReport
        {
            Title = request.Title,
            Scope = request.Scope,
            ReportType = request.ReportType,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            GeneratedByUserID = userId,
            GeneratedBy = userName,
            GeneratedDate = DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow,
            Status = ComplianceReportStatus.Draft,
            Metrics = "{}"
        };

        var created = await repo.CreateAsync(report);

        // Change 7: auto-calculate metrics from audit log if period is specified
        if (request.PeriodStart.HasValue && request.PeriodEnd.HasValue)
        {
            var metricsJson = await BuildMetricsJsonAsync(request.PeriodStart.Value, request.PeriodEnd.Value);
            created.Metrics = metricsJson;
            created = await repo.UpdateAsync(created);
        }

        // Change 4: write audit entry directly to repository
        await WriteAuditAsync("Created ComplianceReport", created.ReportID.ToString(),
            $"Title: {created.Title}, Type: {created.ReportType}");

        return ApiResponse<ComplianceReportViewModel>.Ok(Map(created), "Compliance report created.");
    }

    public async Task<ApiResponse<ComplianceReportViewModel>> UpdateStatusAsync(
        int id, UpdateReportStatusRequest request)
    {
        var report = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Compliance report {id} not found.");

        // Change 2: lock Approved reports
        if (report.Status == ComplianceReportStatus.Approved)
            throw new ValidationException(
                "Approved reports cannot be modified. " +
                "Approved compliance reports are locked and immutable.");

        report.Status = request.Status;
        report.UpdatedDate = DateTime.UtcNow;
        var updated = await repo.UpdateAsync(report);

        // Change 4: audit
        await WriteAuditAsync($"Updated ComplianceReport Status to {request.Status}",
            id.ToString(), $"Title: {report.Title}");

        // Change 3: notify Admin when status moves to InReview
        if (request.Status == ComplianceReportStatus.InReview)
            await NotifyInReviewAsync(id, report.Title);

        return ApiResponse<ComplianceReportViewModel>.Ok(Map(updated), "Report status updated.");
    }

    public async Task<ApiResponse<ComplianceReportViewModel>> ApproveReportAsync(
        int id, ApproveReportRequest request)
    {
        var report = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Compliance report {id} not found.");

        // Change 2: must be InReview before approving
        if (report.Status != ComplianceReportStatus.InReview)
            throw new ValidationException(
                $"Report must be in InReview status before it can be approved. " +
                $"Current status: {report.Status}.");

        // Change 6: require at least one audit entry within the report period
        if (report.PeriodStart.HasValue && report.PeriodEnd.HasValue)
        {
            var periodEntries = await auditRepo.GetAllForMetricsAsync(
                report.PeriodStart.Value, report.PeriodEnd.Value);

            if (!periodEntries.Any())
                throw new ValidationException(
                    $"Cannot approve this report. No audit log entries found between " +
                    $"{report.PeriodStart.Value:yyyy-MM-dd} and {report.PeriodEnd.Value:yyyy-MM-dd}. " +
                    "Please verify audit logging is working correctly.");
        }

        report.ApprovedBy = request.ApprovedBy;
        report.ApprovedDate = DateTime.UtcNow;
        report.Status = ComplianceReportStatus.Approved;
        report.UpdatedDate = DateTime.UtcNow;
        var updated = await repo.UpdateAsync(report);

        // Change 4: audit
        await WriteAuditAsync("Approved ComplianceReport", id.ToString(),
            $"Title: {report.Title}, ApprovedBy: {request.ApprovedBy}");

        // Change 3: notify the report creator
        await NotifyApprovedAsync(id, report.Title, report.GeneratedByUserID);

        return ApiResponse<ComplianceReportViewModel>.Ok(Map(updated), "Report approved.");
    }

    // ── Change 7: Metrics calculation ─────────────────────────────────────────
    private async Task<string> BuildMetricsJsonAsync(DateTime from, DateTime to)
    {
        try
        {
            var entries = (await auditRepo.GetAllForMetricsAsync(from, to)).ToList();

            var byService = entries
                .GroupBy(e => e.ServiceName)
                .ToDictionary(g => g.Key, g => g.Count());

            var byAction = entries
                .GroupBy(e => e.Action)
                .ToDictionary(g => g.Key, g => g.Count());

            var highRiskCount = entries
                .Count(e => e.Action.Contains("Delete", StringComparison.OrdinalIgnoreCase));

            var metrics = new
            {
                totalActions = entries.Count,
                byService,
                byAction,
                highRiskActions = highRiskCount,
                deletedCount = highRiskCount
            };

            return JsonSerializer.Serialize(metrics,
                new JsonSerializerOptions { WriteIndented = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Metrics calculation failed for compliance report.");
            return "{}";
        }
    }

    // ── Mapper ────────────────────────────────────────────────────────────────

    private static ComplianceReportViewModel Map(ComplianceReport r) => new()
    {
        ReportID = r.ReportID,
        Title = r.Title,
        Scope = r.Scope,
        Metrics = r.Metrics,
        GeneratedDate = r.GeneratedDate,
        GeneratedByUserID = r.GeneratedByUserID,
        GeneratedBy = r.GeneratedBy,
        Status = r.Status,
        ReportType = r.ReportType,
        PeriodStart = r.PeriodStart,
        PeriodEnd = r.PeriodEnd,
        CreatedDate = r.CreatedDate,
        UpdatedDate = r.UpdatedDate,
        ApprovedBy = r.ApprovedBy,
        ApprovedDate = r.ApprovedDate
    };
}
