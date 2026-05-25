using System.Net.Http.Json;
using AnalyticsService.DTOs;
using AnalyticsService.Models;
using AnalyticsService.Repositories.Interfaces;
using AnalyticsService.Services.Interfaces;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Helpers;
using ManuTrack.SharedKernel.Responses;
using Microsoft.AspNetCore.Http;

namespace AnalyticsService.Services;

public class AnalyticsServiceImpl(
    IKpiReportRepository kpiRepo,
    IProductionMetricRepository metricRepo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AnalyticsServiceImpl> logger) : IAnalyticsService
{
    private const int MaxPageSize = 20;

    // Change 2: audit log (fire-and-forget)
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
                ServiceName = "AnalyticsService",
                Details = details
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in AnalyticsService."); }
    }

    // ── KPI Reports ───────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<KpiReportViewModel>>> GetAllReportsAsync(string? reportType)
    {
        var reports = await kpiRepo.GetAllAsync(reportType);
        return ApiResponse<IEnumerable<KpiReportViewModel>>.Ok(reports.Select(MapReport));
    }

    public async Task<ApiResponse<KpiReportViewModel>> GetReportByIdAsync(int id)
    {
        var report = await kpiRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"KPI report {id} not found.");
        return ApiResponse<KpiReportViewModel>.Ok(MapReport(report));
    }

    public async Task<ApiResponse<KpiReportViewModel>> GenerateReportAsync(
        GenerateKpiReportRequest request, string generatedBy)
    {
        var report = new KpiReport
        {
            Title = request.Title,
            ReportType = request.ReportType,
            Scope = request.Scope,
            Metrics = request.Metrics,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            GeneratedBy = generatedBy,
            GeneratedDate = DateTime.UtcNow
        };

        var created = await kpiRepo.CreateAsync(report);

        // Change 2: audit log
        await LogAuditAsync("Generated KPI Report", "KpiReport", created.ReportID.ToString(),
            $"Type: {created.ReportType}, GeneratedBy: {generatedBy}");

        return ApiResponse<KpiReportViewModel>.Ok(MapReport(created), "KPI report generated.");
    }

    // ── Production Metrics (Change 3: paginated) ──────────────────────────────

    public async Task<ApiResponse<PagedMetricsViewModel>> GetMetricsAsync(
        string? metricType, string? serviceSource, DateTime? from, DateTime? to,
        int page, int pageSize)
    {
        // clamp pageSize to max allowed
        pageSize = Math.Min(pageSize, MaxPageSize);
        page = Math.Max(page, 1);

        var totalRecords = await metricRepo.CountAsync(metricType, serviceSource, from, to);
        var totalPages = totalRecords == 0 ? 1 : (int)Math.Ceiling((double)totalRecords / pageSize);

        var metrics = await metricRepo.GetAllAsync(metricType, serviceSource, from, to, page, pageSize);

        var paged = new PagedMetricsViewModel
        {
            Data = metrics.Select(MapMetric),
            Pagination = new PaginationViewModel
            {
                CurrentPage  = page,
                PageSize     = pageSize,
                TotalRecords = totalRecords,
                TotalPages   = totalPages
            }
        };

        return ApiResponse<PagedMetricsViewModel>.Ok(paged);
    }

    public async Task<ApiResponse<ProductionMetricViewModel>> RecordMetricAsync(
        RecordMetricRequest request)
    {
        var metric = new ProductionMetric
        {
            MetricType    = request.MetricType,
            MetricName    = request.MetricName,
            Value         = request.Value,
            Unit          = request.Unit,
            ServiceSource = request.ServiceSource,
            EntityID      = request.EntityID,
            RecordedDate  = DateTime.UtcNow
        };

        var created = await metricRepo.CreateAsync(metric);

        // Change 2: audit log
        await LogAuditAsync("Recorded Production Metric", "ProductionMetric",
            created.MetricID.ToString(),
            $"Type: {created.MetricType}, Name: {created.MetricName}, Value: {created.Value} {created.Unit}");

        return ApiResponse<ProductionMetricViewModel>.Ok(MapMetric(created), "Metric recorded.");
    }

    // ── Change 4: Enriched Dashboard ──────────────────────────────────────────

    public async Task<ApiResponse<DashboardSummaryViewModel>> GetDashboardSummaryAsync()
    {
        // Local data (always available)
        var reports    = await kpiRepo.GetAllAsync();
        var totalMetrics = await metricRepo.GetTotalCountAsync();
        var latestKpis = await metricRepo.GetLatestKpisAsync();

        var summary = new DashboardSummaryViewModel
        {
            TotalReports = reports.Count(),
            TotalMetrics = totalMetrics,
            LatestKpis   = latestKpis
        };

        // Run all external calls in parallel — each is isolated; failure → 0
        await Task.WhenAll(
            FetchWorkOrderStatsAsync(summary),
            FetchInventoryStatsAsync(summary),
            FetchQualityStatsAsync(summary),
            FetchComplianceStatsAsync(summary)
        );

        return ApiResponse<DashboardSummaryViewModel>.Ok(summary);
    }

    private async Task FetchWorkOrderStatsAsync(DashboardSummaryViewModel summary)
    {
        try
        {
            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "WorkOrderService");
            var response = await client.GetAsync("api/v1/workorders");
            if (!response.IsSuccessStatusCode) return;

            var result = await response.Content.ReadFromJsonAsync<WOListDto>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var orders = result?.Data ?? [];
            var now = DateTime.UtcNow;

            summary.ActiveWorkOrders    = orders.Count(o => o.Status == "InProgress");
            summary.OverdueWorkOrders   = orders.Count(o => o.IsOverdue);
            summary.CompletedThisMonth  = orders.Count(o =>
                o.Status == "Completed" &&
                o.ActualEndDate.HasValue &&
                o.ActualEndDate.Value.Year  == now.Year &&
                o.ActualEndDate.Value.Month == now.Month);
        }
        catch (Exception ex) { logger.LogWarning(ex, "WorkOrderService unavailable — work order stats will show 0."); }
    }

    private async Task FetchInventoryStatsAsync(DashboardSummaryViewModel summary)
    {
        try
        {
            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "InventoryService");
            var response = await client.GetAsync("api/v1/inventory");
            if (!response.IsSuccessStatusCode) return;

            var result = await response.Content.ReadFromJsonAsync<InvListDto>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var items = result?.Data ?? [];
            summary.LowStockItems   = items.Count(i => i.Status == "LowStock");
            summary.OutOfStockItems = items.Count(i => i.Status == "OutOfStock");
        }
        catch (Exception ex) { logger.LogWarning(ex, "InventoryService unavailable — inventory stats will show 0."); }
    }

    private async Task FetchQualityStatsAsync(DashboardSummaryViewModel summary)
    {
        try
        {
            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "QualityService");
            var response = await client.GetAsync("api/v1/defects");
            if (!response.IsSuccessStatusCode) return;

            var result = await response.Content.ReadFromJsonAsync<DefectListDto>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var defects = result?.Data ?? [];
            summary.OpenDefects     = defects.Count(d => d.Status == "Open");
            summary.CriticalDefects = defects.Count(d => d.Severity == "Critical");
        }
        catch (Exception ex) { logger.LogWarning(ex, "QualityService unavailable — quality stats will show 0."); }
    }

    private async Task FetchComplianceStatsAsync(DashboardSummaryViewModel summary)
    {
        try
        {
            var client = ServiceHelper.CreateAuthorizedClient(httpClientFactory, httpContextAccessor, "ComplianceService");
            var response = await client.GetAsync("api/v1/compliance-reports");
            if (!response.IsSuccessStatusCode) return;

            var result = await response.Content.ReadFromJsonAsync<ComplianceListDto>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var reports = result?.Data ?? [];
            summary.PendingComplianceReports =
                reports.Count(r => r.Status == "Draft" || r.Status == "InReview");
        }
        catch (Exception ex) { logger.LogWarning(ex, "ComplianceService unavailable — compliance stats will show 0."); }
    }

    // ── Mappers ───────────────────────────────────────────────────────────────

    private static KpiReportViewModel MapReport(KpiReport r) => new()
    {
        ReportID      = r.ReportID,
        Title         = r.Title,
        ReportType    = r.ReportType,
        Scope         = r.Scope,
        Metrics       = r.Metrics,
        GeneratedDate = r.GeneratedDate,
        GeneratedBy   = r.GeneratedBy,
        PeriodStart   = r.PeriodStart,
        PeriodEnd     = r.PeriodEnd
    };

    private static ProductionMetricViewModel MapMetric(ProductionMetric m) => new()
    {
        MetricID      = m.MetricID,
        MetricType    = m.MetricType,
        MetricName    = m.MetricName,
        Value         = m.Value,
        Unit          = m.Unit,
        ServiceSource = m.ServiceSource,
        EntityID      = m.EntityID,
        RecordedDate  = m.RecordedDate
    };

    // ── Local DTOs for cross-service deserialization ───────────────────────────

    private sealed class WOListDto
    {
        public List<WODto>? Data { get; set; }
    }
    private sealed class WODto
    {
        public string Status { get; set; } = string.Empty;
        public bool IsOverdue { get; set; }
        public DateTime? ActualEndDate { get; set; }
    }

    private sealed class InvListDto
    {
        public List<InvDto>? Data { get; set; }
    }
    private sealed class InvDto
    {
        public string Status { get; set; } = string.Empty;
    }

    private sealed class DefectListDto
    {
        public List<DefectDto>? Data { get; set; }
    }
    private sealed class DefectDto
    {
        public string Status { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
    }

    private sealed class ComplianceListDto
    {
        public List<ComplianceDto>? Data { get; set; }
    }
    private sealed class ComplianceDto
    {
        public string Status { get; set; } = string.Empty;
    }
}
