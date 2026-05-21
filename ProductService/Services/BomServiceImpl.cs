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

public class BomServiceImpl(
    IBomRepository bomRepo,
    IProductRepository productRepo,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<BomServiceImpl> logger) : IBomService
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

    private HttpClient CreateAuthorizedClient(string clientName)
    {
        var client = httpClientFactory.CreateClient(clientName);
        var token = GetBearerToken();
        if (token != null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── Change 4: Audit logging (fire-and-forget) ────────────────────────────
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
                ServiceName = "ProductService",
                Details = details
            });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Audit log failed in BomService."); }
    }

    // ── Change 5: Low stock alert (fire-and-forget) ───────────────────────────
    private async Task CheckLowStockAndNotifyAsync(int componentId, decimal requiredQuantity)
    {
        try
        {
            var (userId, _) = GetCurrentUser();
            if (userId == 0) return;

            // Query InventoryService for all items and filter by componentId
            var invClient = CreateAuthorizedClient("InventoryService");
            var invResponse = await invClient.GetAsync("api/v1/inventory");
            if (!invResponse.IsSuccessStatusCode) return;

            var invResult = await invResponse.Content
                .ReadFromJsonAsync<ApiResponse<IEnumerable<InventoryItemDto>>>(
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var totalStock = invResult?.Data?
                .Where(i => i.ProductID == componentId)
                .Sum(i => i.QuantityOnHand) ?? 0;

            if (totalStock < requiredQuantity)
            {
                var notifyClient = CreateAuthorizedClient("NotificationService");
                await notifyClient.PostAsJsonAsync("api/v1/notifications", new
                {
                    UserID = userId,
                    Title = "Low Stock Alert",
                    Message = $"Component (ID: {componentId}) has only {totalStock} units in stock, " +
                              $"but the BOM requires {requiredQuantity} units.",
                    Category = "Inventory"
                });
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Low stock notification failed for component {ComponentId}.", componentId); }
    }

    // ── CRUD ────────────────────────────────────────────────────────────────

    public async Task<ApiResponse<IEnumerable<BomViewModel>>> GetAllBomsAsync(int? productId, string? status)
    {
        var boms = await bomRepo.GetAllAsync(productId, status);
        return ApiResponse<IEnumerable<BomViewModel>>.Ok(boms.Select(Map));
    }

    public async Task<ApiResponse<BomViewModel>> GetBomByIdAsync(int id)
    {
        var bom = await bomRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"BOM {id} not found.");
        return ApiResponse<BomViewModel>.Ok(Map(bom));
    }

    public async Task<ApiResponse<IEnumerable<BomViewModel>>> GetBomsByProductIdAsync(int productId)
    {
        if (!await productRepo.ExistsAsync(productId))
            throw new NotFoundException($"Product {productId} not found.");

        var boms = await bomRepo.GetByProductIdAsync(productId);
        return ApiResponse<IEnumerable<BomViewModel>>.Ok(boms.Select(Map));
    }

    public async Task<ApiResponse<BomViewModel>> CreateBomAsync(CreateBomRequest request)
    {
        // Change 2: product must exist AND be Active before allowing BOM creation
        var product = await productRepo.GetByIdAsync(request.ProductID)
            ?? throw new NotFoundException($"Product {request.ProductID} not found.");

        if (product.Status != ProductStatus.Active)
            throw new ValidationException(
                $"Product '{product.Name}' is not Active (current status: {product.Status}). " +
                "A BOM can only be created for Active products.");

        if (!await productRepo.ExistsAsync(request.ComponentID))
            throw new NotFoundException($"Component product {request.ComponentID} not found.");

        if (request.ProductID == request.ComponentID)
            throw new ValidationException("A product cannot be its own component.");

        var bom = new Bom
        {
            ProductID = request.ProductID,
            ComponentID = request.ComponentID,
            Quantity = request.Quantity,
            Version = request.Version,
            Notes = request.Notes,
            Status = ProductStatus.Draft,   // Change 1: default to Draft
            CreatedDate = DateTime.UtcNow
        };

        var created = await bomRepo.CreateAsync(bom);
        var full = await bomRepo.GetByIdAsync(created.BOMID);

        // Change 4: audit log
        await LogAuditAsync("Created BOM", "BOM", created.BOMID.ToString(),
            $"ProductID: {created.ProductID}, ComponentID: {created.ComponentID}, Quantity: {created.Quantity}");

        // Change 5: low stock alert for the component
        await CheckLowStockAndNotifyAsync(created.ComponentID, created.Quantity);

        return ApiResponse<BomViewModel>.Ok(Map(full!), "BOM entry created successfully.");
    }

    public async Task<ApiResponse<BomViewModel>> UpdateBomAsync(int id, UpdateBomRequest request)
    {
        var bom = await bomRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"BOM {id} not found.");

        if (request.Quantity.HasValue) bom.Quantity = request.Quantity.Value;
        if (request.Version != null) bom.Version = request.Version;
        if (request.Notes != null) bom.Notes = request.Notes;

        var updated = await bomRepo.UpdateAsync(bom);

        // Change 4: audit log
        await LogAuditAsync("Updated BOM", "BOM", id.ToString(),
            $"Version: {updated.Version}, Quantity: {updated.Quantity}");

        // Change 5: re-check low stock when quantity is updated
        if (request.Quantity.HasValue)
            await CheckLowStockAndNotifyAsync(updated.ComponentID, updated.Quantity);

        return ApiResponse<BomViewModel>.Ok(Map(updated), "BOM updated successfully.");
    }

    public async Task<ApiResponse<BomViewModel>> UpdateBomStatusAsync(int id, UpdateBomStatusRequest request)
    {
        var bom = await bomRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"BOM {id} not found.");

        bom.Status = request.Status;
        var updated = await bomRepo.UpdateAsync(bom);

        // Change 4: audit log
        await LogAuditAsync("Updated BOM Status", "BOM", id.ToString(),
            $"New Status: {request.Status}");

        return ApiResponse<BomViewModel>.Ok(Map(updated), "BOM status updated.");
    }

    public async Task<ApiResponse> DeleteBomAsync(int id)
    {
        var bom = await bomRepo.GetByIdAsync(id)
            ?? throw new NotFoundException($"BOM {id} not found.");

        await bomRepo.DeleteAsync(bom);

        // Change 4: audit log
        await LogAuditAsync("Deleted BOM", "BOM", id.ToString(),
            $"ProductID: {bom.ProductID}, ComponentID: {bom.ComponentID}");

        return ApiResponse.Ok("BOM deleted successfully.");
    }

    // ── Mapper ──────────────────────────────────────────────────────────────

    private static BomViewModel Map(Bom b) => new()
    {
        BOMID = b.BOMID,
        ProductID = b.ProductID,
        ProductName = b.Product?.Name ?? string.Empty,
        ComponentID = b.ComponentID,
        ComponentName = b.Component?.Name ?? string.Empty,
        Quantity = b.Quantity,
        Version = b.Version,
        Status = b.Status,
        Notes = b.Notes,
        CreatedDate = b.CreatedDate
    };

    // ── Local DTO for InventoryService response deserialization ──────────────
    private sealed class InventoryItemDto
    {
        public int ProductID { get; set; }
        public decimal QuantityOnHand { get; set; }
    }
}
