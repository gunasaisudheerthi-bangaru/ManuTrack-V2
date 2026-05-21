using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ProductService.Enums;
using ManuTrack.SharedKernel.Exceptions;
using ManuTrack.SharedKernel.Responses;
using ProductService.DTOs;
using ProductService.Models;
using ProductService.Repositories.Interfaces;
using ProductService.Services.Interfaces;

namespace ProductService.Services;

public class ProductServiceImpl(
    IProductRepository repo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ProductServiceImpl> logger) : IProductService
{
    // ── Internal helpers ────────────────────────────────────────────────────

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

    // ── Change 4: Audit logging (fire-and-forget) ────────────────────────────
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
                ServiceName = "ProductService",
                Details = details
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in ProductService."); }
    }

    // ── CRUD ────────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<ProductViewModel>>> GetAllProductsAsync(string? category, string? status)
    {
        var products = await repo.GetAllAsync(category, status);
        return ApiResponse<IEnumerable<ProductViewModel>>.Ok(products.Select(Map));
    }

    public async Task<ApiResponse<ProductViewModel>> GetProductByIdAsync(int id)
    {
        var product = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Product {id} not found.");
        return ApiResponse<ProductViewModel>.Ok(Map(product));
    }

    public async Task<ApiResponse<ProductViewModel>> CreateProductAsync(CreateProductRequest request)
    {
        var existing = await repo.GetByNameAsync(request.Name);
        if (existing != null)
            throw new ConflictException($"Product with name '{request.Name}' already exists.");

        var product = new Product
        {
            Name = request.Name,
            Category = request.Category,
            Version = request.Version,
            Description = request.Description,
            Status = ProductStatus.Draft,
            CreatedDate = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(product);

        // Change 4: audit log
        await LogAuditAsync("Created Product", "Product", created.ProductID.ToString(),
            $"Name: {created.Name}, Category: {created.Category}");

        return ApiResponse<ProductViewModel>.Ok(Map(created), "Product created successfully.");
    }

    public async Task<ApiResponse<ProductViewModel>> UpdateProductAsync(int id, UpdateProductRequest request)
    {
        var product = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Product {id} not found.");

        if (request.Name != null) product.Name = request.Name;
        if (request.Category != null) product.Category = request.Category;
        if (request.Version != null) product.Version = request.Version;
        if (request.Description != null) product.Description = request.Description;
        product.ModifiedDate = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(product);

        // Change 4: audit log
        await LogAuditAsync("Updated Product", "Product", id.ToString(),
            $"Name: {updated.Name}, Status: {updated.Status}");

        return ApiResponse<ProductViewModel>.Ok(Map(updated), "Product updated successfully.");
    }

    public async Task<ApiResponse<ProductViewModel>> UpdateProductStatusAsync(int id, UpdateProductStatusRequest request)
    {
        var product = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Product {id} not found.");

        product.Status = request.Status;
        product.ModifiedDate = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(product);

        // Change 4: audit log
        await LogAuditAsync("Updated Product Status", "Product", id.ToString(),
            $"New Status: {request.Status}");

        return ApiResponse<ProductViewModel>.Ok(Map(updated), "Product status updated.");
    }

    public async Task<ApiResponse> DeleteProductAsync(int id)
    {
        var product = await repo.GetByIdAsync(id)
            ?? throw new NotFoundException($"Product {id} not found.");

        await repo.DeleteAsync(product);

        // Change 4: audit log
        await LogAuditAsync("Deleted Product", "Product", id.ToString(),
            $"Name: {product.Name}");

        return ApiResponse.Ok("Product deleted successfully.");
    }

    // ── Mapper ──────────────────────────────────────────────────────────────

    private static ProductViewModel Map(Product p) => new()
    {
        ProductID = p.ProductID,
        Name = p.Name,
        Category = p.Category,
        Version = p.Version,
        Status = p.Status,
        Description = p.Description,
        CreatedDate = p.CreatedDate,
        ModifiedDate = p.ModifiedDate
    };
}
