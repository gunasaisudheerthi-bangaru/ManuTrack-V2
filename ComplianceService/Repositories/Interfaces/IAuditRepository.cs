using ComplianceService.Models;

namespace ComplianceService.Repositories.Interfaces;

public interface IAuditRepository
{
    Task<IEnumerable<AuditEntry>> GetAllAsync(
        string? userId = null,
        string? serviceName = null,
        DateTime? from = null,
        DateTime? to = null,
        string? entityType = null,
        string? action = null,
        string? entityId = null,
        int page = 1,
        int pageSize = 20);

    Task<int> CountAsync(
        string? userId = null,
        string? serviceName = null,
        DateTime? from = null,
        DateTime? to = null,
        string? entityType = null,
        string? action = null,
        string? entityId = null);

    Task<AuditEntry?> GetByIdAsync(int id);
    Task<AuditEntry> CreateAsync(AuditEntry entry);

    Task<IEnumerable<AuditEntry>> GetAllForMetricsAsync(DateTime from, DateTime to);
}
