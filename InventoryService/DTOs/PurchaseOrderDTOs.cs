using System.ComponentModel.DataAnnotations;

namespace InventoryService.DTOs;

public class CreatePurchaseOrderRequest : IValidatableObject
{
    [Range(1, int.MaxValue, ErrorMessage = "SupplierRefID must be a positive integer.")]
    public int? SupplierRefID { get; set; }

    [MaxLength(100, ErrorMessage = "SupplierID cannot exceed 100 characters.")]
    public string? SupplierID { get; set; }

    [MaxLength(200, ErrorMessage = "Supplier name cannot exceed 200 characters.")]
    public string? SupplierName { get; set; }

    [Required(ErrorMessage = "Expected delivery date is required.")]
    public DateTime ExpectedDeliveryDate { get; set; }

    [MaxLength(1000, ErrorMessage = "Notes cannot exceed 1000 characters.")]
    public string? Notes { get; set; }

    [Required(ErrorMessage = "At least one item is required.")]
    [MinLength(1, ErrorMessage = "At least one order item must be provided.")]
    public List<CreatePurchaseOrderItemRequest> Items { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SupplierRefID == null && string.IsNullOrWhiteSpace(SupplierName))
            yield return new ValidationResult(
                "Either SupplierRefID or SupplierName must be provided.",
                [nameof(SupplierRefID), nameof(SupplierName)]);

        if (ExpectedDeliveryDate <= DateTime.UtcNow.Date)
            yield return new ValidationResult(
                "Expected delivery date must be a future date.",
                [nameof(ExpectedDeliveryDate)]);
    }
}

public class CreatePurchaseOrderItemRequest
{
    [Required(ErrorMessage = "InventoryID is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "InventoryID must be a positive integer.")]
    public int InventoryID { get; set; }

    [Required(ErrorMessage = "ProductID is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "ProductID must be a positive integer.")]
    public int ProductID { get; set; }

    [Required(ErrorMessage = "Product name is required.")]
    [MinLength(2, ErrorMessage = "Product name must be at least 2 characters.")]
    [MaxLength(200, ErrorMessage = "Product name cannot exceed 200 characters.")]
    public string ProductName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Quantity is required.")]
    [Range(0.0001, 999999.9999, ErrorMessage = "Quantity must be greater than 0.")]
    public decimal Quantity { get; set; }

    [Required(ErrorMessage = "Unit price is required.")]
    [Range(0.01, 9999999.99, ErrorMessage = "Unit price must be between 0.01 and 9,999,999.99.")]
    public decimal UnitPrice { get; set; }
}

public class UpdatePurchaseOrderStatusRequest
{
    [Required(ErrorMessage = "Status is required.")]
    [RegularExpression("^(Pending|Approved|Ordered|Received|Cancelled)$",
        ErrorMessage = "Status must be one of: Pending, Approved, Ordered, Received, Cancelled.")]
    public string Status { get; set; } = string.Empty;
}

public class PurchaseOrderViewModel
{
    public int POID { get; set; }
    public int? SupplierRefID { get; set; }
    public string SupplierID { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public DateTime ExpectedDeliveryDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public List<PurchaseOrderItemViewModel> Items { get; set; } = new();
}

public class PurchaseOrderItemViewModel
{
    public int POItemID { get; set; }
    public int InventoryID { get; set; }
    public int ProductID { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public decimal ReceivedQty { get; set; }
    public DateTime CreatedDate { get; set; }
}
