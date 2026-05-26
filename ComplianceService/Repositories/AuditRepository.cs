using ComplianceService.Data;
using ComplianceService.Models;
using ComplianceService.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ComplianceService.Repositories;

public class AuditRepository(AuditDbContext db) : IAuditRepository
{
    private IQueryable<AuditEntry> BuildQuery(
        string? userId,
        string? serviceName,
        DateTime? from,
        DateTime? to,
        string? entityType,
        string? action,
        string? entityId)
    {
        var query = db.AuditEntries.AsQueryable();

        if (!string.IsNullOrWhiteSpace(userId) && int.TryParse(userId, out var userIdInt))
            query = query.Where(a => a.UserID == userIdInt);
        if (!string.IsNullOrWhiteSpace(serviceName))
            query = query.Where(a => a.ServiceName == serviceName);
        if (from.HasValue)
            query = query.Where(a => a.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(a => a.Timestamp <= to.Value);
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action.Contains(action));
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(a => a.EntityID == entityId);

        return query;
    }

    public async Task<IEnumerable<AuditEntry>> GetAllAsync(
        string? userId = null,
        string? serviceName = null,
        DateTime? from = null,
        DateTime? to = null,
        string? entityType = null,
        string? action = null,
        string? entityId = null,
        int page = 1,
        int pageSize = 20)
    {
        return await BuildQuery(userId, serviceName, from, to, entityType, action, entityId)
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> CountAsync(
        string? userId = null,
        string? serviceName = null,
        DateTime? from = null,
        DateTime? to = null,
        string? entityType = null,
        string? action = null,
        string? entityId = null)
    {
        return await BuildQuery(userId, serviceName, from, to, entityType, action, entityId)
            .CountAsync();
    }

    public async Task<AuditEntry?> GetByIdAsync(int id) => await db.AuditEntries.FindAsync(id);

    public async Task<AuditEntry> CreateAsync(AuditEntry entry)
    {
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    public async Task<IEnumerable<AuditEntry>> GetAllForMetricsAsync(DateTime from, DateTime to)
    {
        return await db.AuditEntries
            .Where(a => a.Timestamp >= from && a.Timestamp <= to)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();
    }
}
