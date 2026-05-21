using System.ComponentModel.DataAnnotations;

namespace ComplianceService.DTOs;

public class CreateComplianceReportRequest : IValidatableObject
{
    [Required(ErrorMessage = "Title is required.")]
    [MinLength(3, ErrorMessage = "Title must be at least 3 characters.")]
    [MaxLength(200, ErrorMessage = "Title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Scope is required.")]
    [MinLength(5, ErrorMessage = "Scope must be at least 5 characters.")]
    [MaxLength(500, ErrorMessage = "Scope cannot exceed 500 characters.")]
    public string Scope { get; set; } = string.Empty;

    [Required(ErrorMessage = "Report type is required.")]
    [RegularExpression("^(Safety|Quality|Environmental|Regulatory|Internal|External)$",
        ErrorMessage = "ReportType must be one of: Safety, Quality, Environmental, Regulatory, Internal, External.")]
    public string ReportType { get; set; } = string.Empty;

    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }

    [MaxLength(8000, ErrorMessage = "Metrics JSON cannot exceed 8000 characters.")]
    public string Metrics { get; set; } = "{}";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PeriodStart.HasValue && PeriodEnd.HasValue && PeriodEnd.Value <= PeriodStart.Value)
            yield return new ValidationResult(
                "PeriodEnd must be after PeriodStart.",
                [nameof(PeriodEnd)]);
    }
}

public class UpdateReportStatusRequest
{
    [Required(ErrorMessage = "Status is required.")]
    [RegularExpression("^(Draft|InReview|Approved|Closed)$",
        ErrorMessage = "Status must be one of: Draft, InReview, Approved, Closed.")]
    public string Status { get; set; } = string.Empty;
}

public class ApproveReportRequest
{
    [Required(ErrorMessage = "ApprovedBy is required.")]
    [MinLength(2, ErrorMessage = "ApprovedBy must be at least 2 characters.")]
    [MaxLength(200, ErrorMessage = "ApprovedBy cannot exceed 200 characters.")]
    public string ApprovedBy { get; set; } = string.Empty;
}

public class LogAuditEntryRequest
{
    [Required(ErrorMessage = "UserID is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "UserID must be a positive number.")]
    public int UserID { get; set; }

    [Required(ErrorMessage = "User name is required.")]
    [MinLength(2, ErrorMessage = "User name must be at least 2 characters.")]
    [MaxLength(200, ErrorMessage = "User name cannot exceed 200 characters.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Action is required.")]
    [MinLength(3, ErrorMessage = "Action must be at least 3 characters.")]
    [MaxLength(200, ErrorMessage = "Action cannot exceed 200 characters.")]
    public string Action { get; set; } = string.Empty;

    [Required(ErrorMessage = "Entity type is required.")]
    [MinLength(2, ErrorMessage = "Entity type must be at least 2 characters.")]
    [MaxLength(100, ErrorMessage = "Entity type cannot exceed 100 characters.")]
    public string EntityType { get; set; } = string.Empty;

    [Required(ErrorMessage = "Entity ID is required.")]
    [MaxLength(100, ErrorMessage = "Entity ID cannot exceed 100 characters.")]
    public string EntityID { get; set; } = string.Empty;

    [Required(ErrorMessage = "Service name is required.")]
    [RegularExpression(
        "^(AuthService|ProductService|WorkOrderService|InventoryService|QualityService|ComplianceService|AnalyticsService|NotificationService)$",
        ErrorMessage = "ServiceName must be a valid ManuTrack microservice name.")]
    public string ServiceName { get; set; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "Details cannot exceed 2000 characters.")]
    public string? Details { get; set; }
}

public class ComplianceReportViewModel
{
    public int ReportID { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Metrics { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; }
    public int GeneratedByUserID { get; set; }
    public string GeneratedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty;
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
}

public class AuditEntryViewModel
{
    public int AuditID { get; set; }
    public int UserID { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityID { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; }
}
