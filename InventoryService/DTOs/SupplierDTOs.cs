using System.ComponentModel.DataAnnotations;

namespace InventoryService.DTOs;

public class CreateSupplierRequest
{
    [Required(ErrorMessage = "Supplier name is required.")]
    [MinLength(2, ErrorMessage = "Name must be at least 2 characters.")]
    [MaxLength(200, ErrorMessage = "Name cannot exceed 200 characters.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200, ErrorMessage = "Contact person cannot exceed 200 characters.")]
    public string? ContactPerson { get; set; }

    [MaxLength(50, ErrorMessage = "Phone cannot exceed 50 characters.")]
    [RegularExpression(@"^\+?[0-9]{10,15}$", ErrorMessage = "Phone must be 10 to 15 digits. Optionally start with +.")]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "Invalid email format.")]
    [MaxLength(200, ErrorMessage = "Email cannot exceed 200 characters.")]
    public string? Email { get; set; }

    [MaxLength(500, ErrorMessage = "Address cannot exceed 500 characters.")]
    public string? Address { get; set; }
}

public class UpdateSupplierRequest
{
    [MinLength(2, ErrorMessage = "Name must be at least 2 characters.")]
    [MaxLength(200, ErrorMessage = "Name cannot exceed 200 characters.")]
    public string? Name { get; set; }

    [MaxLength(200, ErrorMessage = "Contact person cannot exceed 200 characters.")]
    public string? ContactPerson { get; set; }

    [MaxLength(50, ErrorMessage = "Phone cannot exceed 50 characters.")]
    [RegularExpression(@"^\+?[0-9]{10,15}$", ErrorMessage = "Phone must be 10 to 15 digits. Optionally start with +.")]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "Invalid email format.")]
    [MaxLength(200, ErrorMessage = "Email cannot exceed 200 characters.")]
    public string? Email { get; set; }

    [MaxLength(500, ErrorMessage = "Address cannot exceed 500 characters.")]
    public string? Address { get; set; }

    public bool? IsActive { get; set; }
}

public class SupplierViewModel
{
    public int SupplierID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
