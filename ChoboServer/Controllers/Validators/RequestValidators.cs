using Chobo.Contracts;
using ChoboServer.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ChoboServer.Controllers.Validators;

public sealed class FluentValidationActionFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var failures = new List<ValidationFailure>();
        foreach (var argument in context.ActionArguments.Values.Where(x => x is not null))
        {
            var validatorType = typeof(IValidator<>).MakeGenericType(argument!.GetType());
            if (services.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(validationContext, context.HttpContext.RequestAborted);
            failures.AddRange(result.Errors.Where(x => x is not null));
        }

        if (failures.Count > 0)
        {
            context.Result = new BadRequestObjectResult(new ErrorResponse(string.Join("; ", failures.Select(x => x.ErrorMessage).Distinct())));
            return;
        }

        await next();
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName).RequiredName(128);
    }
}

public sealed class CreateAccessTokenRequestValidator : AbstractValidator<CreateAccessTokenRequest>
{
    public CreateAccessTokenRequestValidator()
    {
        RuleFor(x => x.Name).RequiredName(128);
    }
}

public sealed class UpsertClusterRequestValidator : AbstractValidator<UpsertClusterRequest>
{
    public UpsertClusterRequestValidator()
    {
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.AccessNodes).NotNull().NotEmpty().WithMessage("At least one access node is required.");
        RuleForEach(x => x.AccessNodes).SetValidator(new UpsertAccessNodeRequestValidator());
        RuleFor(x => x.UserName).MaximumLength(256).When(x => x.UserName is not null);
        RuleFor(x => x.Password).MaximumLength(4096).When(x => x.Password is not null);
        RuleFor(x => x.BackupRestoreMaxDop).InclusiveBetween(1, 1024).When(x => x.BackupRestoreMaxDop is not null);
        RuleFor(x => x.ClickHouseClusterName).MaximumLength(256).When(x => x.ClickHouseClusterName is not null);
    }
}

public sealed class UpsertAccessNodeRequestValidator : AbstractValidator<UpsertAccessNodeRequest>
{
    public UpsertAccessNodeRequestValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(512);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
    }
}

public sealed class UpdateClusterCredentialsRequestValidator : AbstractValidator<UpdateClusterCredentialsRequest>
{
    public UpdateClusterCredentialsRequestValidator()
    {
        RuleFor(x => x.UserName).MaximumLength(256).When(x => x.UserName is not null);
        RuleFor(x => x.Password).MaximumLength(4096).When(x => x.Password is not null);
    }
}

public sealed class UpsertS3TargetRequestValidator : AbstractValidator<UpsertS3TargetRequest>
{
    public UpsertS3TargetRequestValidator()
    {
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.Endpoint)
            .NotEmpty()
            .MaximumLength(2048)
            .Must(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Endpoint must be an absolute HTTP or HTTPS URI.");
        RuleFor(x => x.Region).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Bucket).NotEmpty().MaximumLength(255);
        RuleFor(x => x.PathPrefix).MaximumLength(1024).When(x => x.PathPrefix is not null);
        RuleFor(x => x.AccessKey).MaximumLength(4096).When(x => x.AccessKey is not null);
        RuleFor(x => x.SecretKey).MaximumLength(4096).When(x => x.SecretKey is not null);
    }
}

public sealed class UpsertPolicyRequestValidator : AbstractValidator<UpsertPolicyRequest>
{
    public UpsertPolicyRequestValidator()
    {
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.SourceClusterId).NotEmpty();
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.Selector).NotNull().SetValidator(new PolicySelectorValidator());
        RuleFor(x => x.Retention).SetValidator(new BackupRetentionDtoValidator()!).When(x => x.Retention is not null);
        RuleFor(x => x.FailedBackupRetentionMode).IsInEnum();
    }
}

public sealed class BackupRetentionDtoValidator : AbstractValidator<BackupRetentionDto>
{
    public BackupRetentionDtoValidator()
    {
        RuleFor(x => x.FullRetentionMinutes).GreaterThan(0).When(x => x.FullRetentionMinutes is not null);
        RuleFor(x => x.IncrementalRetentionMinutes).GreaterThan(0).When(x => x.IncrementalRetentionMinutes is not null);
        RuleFor(x => x.MinBackupsToKeep).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MinFullBackupsToKeep).GreaterThanOrEqualTo(0);
    }
}

public sealed class PolicySelectorValidator : AbstractValidator<PolicySelector>
{
    public PolicySelectorValidator()
    {
        RuleFor(x => x.Version).Equal(1);
        RuleFor(x => x.Rules).NotNull().NotEmpty();
        RuleForEach(x => x.Rules).SetValidator(new PolicySelectorRuleValidator());
    }
}

public sealed class PolicySelectorRuleValidator : AbstractValidator<PolicySelectorRule>
{
    public PolicySelectorRuleValidator()
    {
        RuleFor(x => x.Action).IsInEnum();
        RuleFor(x => x.Database).NotNull().SetValidator(new SelectorPatternValidator());
        RuleFor(x => x.Table).NotNull().SetValidator(new SelectorPatternValidator());
    }
}

public sealed class SelectorPatternValidator : AbstractValidator<SelectorPattern>
{
    public SelectorPatternValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Value).NotNull().MaximumLength(512);
        RuleFor(x => x.Value)
            .NotEmpty()
            .When(x => x.Kind is PolicyMatchKind.Exact or PolicyMatchKind.Wildcard)
            .WithMessage("Selector value is required.");
    }
}

public sealed class PolicyEvaluationRequestValidator : AbstractValidator<PolicyEvaluationRequest>
{
    public PolicyEvaluationRequestValidator()
    {
        RuleFor(x => x.Inventory).NotNull().SetValidator(new PolicyInventoryValidator());
    }
}

public sealed class PolicyInventoryValidator : AbstractValidator<PolicyInventory>
{
    public PolicyInventoryValidator()
    {
        RuleFor(x => x.Tables).NotNull();
        RuleForEach(x => x.Tables).SetValidator(new PolicyInventoryTableValidator());
    }
}

public sealed class PolicyInventoryTableValidator : AbstractValidator<PolicyInventoryTable>
{
    public PolicyInventoryTableValidator()
    {
        RuleFor(x => x.Database).NotEmpty().MaximumLength(512);
        RuleFor(x => x.Table).NotEmpty().MaximumLength(512);
    }
}

public sealed class UpsertScheduleRequestValidator : AbstractValidator<UpsertScheduleRequest>
{
    public UpsertScheduleRequestValidator()
    {
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.PolicyId).NotEmpty();
        RuleFor(x => x.BackupType).IsInEnum();
        RuleFor(x => x.CronExpression).ValidQuartzCron();
        RuleFor(x => x.TimeZoneId).ValidTimeZone();
        RuleFor(x => x.MissedRunGracePeriod).GreaterThan(TimeSpan.Zero).When(x => x.MissedRunGracePeriod is not null);
        RuleFor(x => x.Description).MaximumLength(2048).When(x => x.Description is not null);
    }
}

public sealed class ValidateScheduleCronRequestValidator : AbstractValidator<ValidateScheduleCronRequest>
{
    public ValidateScheduleCronRequestValidator()
    {
        RuleFor(x => x.CronExpression).NotEmpty();
        RuleFor(x => x.TimeZoneId).NotEmpty();
    }
}

public sealed class ManualBackupRequestValidator : AbstractValidator<ManualBackupRequest>
{
    public ManualBackupRequestValidator()
    {
        RuleFor(x => x.ClusterId).NotEmpty();
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.Selector).NotNull().SetValidator(new PolicySelectorValidator());
        RuleFor(x => x.BackupType).IsInEnum();
    }
}

public sealed class InitiateRestoreRequestValidator : AbstractValidator<InitiateRestoreRequest>
{
    public InitiateRestoreRequestValidator()
    {
        RuleFor(x => x.BackupId).NotEmpty();
        RuleFor(x => x.TargetClusterId).NotEmpty();
        RuleFor(x => x.Database).MaximumLength(512).When(x => x.Database is not null);
        RuleFor(x => x.Table).MaximumLength(512).When(x => x.Table is not null);
        RuleFor(x => x.TargetDatabase).MaximumLength(512).When(x => x.TargetDatabase is not null);
        RuleFor(x => x.TargetTable).MaximumLength(512).When(x => x.TargetTable is not null);
        RuleFor(x => x.Layout).IsInEnum().When(x => x.Layout is not null);
        RuleFor(x => x.SourceShard).GreaterThan(0).When(x => x.SourceShard is not null);
        RuleFor(x => x.TargetShard).GreaterThan(0).When(x => x.TargetShard is not null);
        RuleFor(x => x)
            .Must(x => string.IsNullOrWhiteSpace(x.TargetDatabase) == string.IsNullOrWhiteSpace(x.TargetTable))
            .WithMessage("TargetDatabase and TargetTable must be provided together.");
    }
}

public sealed class RecoverBackupMetadataFromPathRequestValidator : AbstractValidator<RecoverBackupMetadataFromPathRequest>
{
    public RecoverBackupMetadataFromPathRequestValidator()
    {
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.BackupPath).NotEmpty().MaximumLength(4096);
    }
}

public sealed class RecoverBackupMetadataScanRequestValidator : AbstractValidator<RecoverBackupMetadataScanRequest>
{
    public RecoverBackupMetadataScanRequestValidator()
    {
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.ScanRoot).NotEmpty().MaximumLength(4096);
    }
}

public sealed class ClearApplicationLogsRequestValidator : AbstractValidator<ClearApplicationLogsRequest>
{
    public ClearApplicationLogsRequestValidator()
    {
        RuleFor(x => x.Before).NotEqual(default(DateTimeOffset));
    }
}

public sealed class ClearAuditEntriesRequestValidator : AbstractValidator<ClearAuditEntriesRequest>
{
    public ClearAuditEntriesRequestValidator()
    {
        RuleFor(x => x.Before).NotEqual(default(DateTimeOffset));
    }
}

public sealed class ExportEnvelopeValidator : AbstractValidator<ExportEnvelope>
{
    public ExportEnvelopeValidator()
    {
        RuleFor(x => x.ExportVersion).GreaterThan(0);
        RuleFor(x => x.SchemaVersion).GreaterThan(0);
        RuleFor(x => x.GeneratedAt).NotEqual(default(DateTimeOffset));
        RuleFor(x => x.ProductVersion).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Data).NotNull().SetValidator(new ExportPayloadValidator());
    }
}

public sealed class ExportPayloadValidator : AbstractValidator<ExportPayload>
{
    public ExportPayloadValidator()
    {
        RuleFor(x => x.Users).NotNull();
        RuleFor(x => x.AccessTokens).NotNull();
        RuleFor(x => x.Clusters).NotNull();
        RuleFor(x => x.BackupTargets).NotNull();
        RuleFor(x => x.BackupPolicies).NotNull();
        RuleFor(x => x.BackupSchedules).NotNull();
        RuleFor(x => x.Audits).NotNull();
        RuleFor(x => x.Logs).NotNull();
        RuleForEach(x => x.Users).SetValidator(new UserExportValidator());
        RuleForEach(x => x.AccessTokens).SetValidator(new AccessTokenExportValidator());
        RuleForEach(x => x.Clusters).SetValidator(new ClusterExportValidator());
        RuleForEach(x => x.BackupTargets).SetValidator(new BackupTargetExportValidator());
        RuleForEach(x => x.BackupPolicies).SetValidator(new BackupPolicyExportValidator());
        RuleForEach(x => x.BackupSchedules).SetValidator(new BackupScheduleExportValidator());
    }
}

public sealed class UserExportValidator : AbstractValidator<UserExport>
{
    public UserExportValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.UserName).RequiredName(128);
        RuleFor(x => x.CreatedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class AccessTokenExportValidator : AbstractValidator<AccessTokenExport>
{
    public AccessTokenExportValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Name).RequiredName(128);
        RuleFor(x => x.TokenHash).NotEmpty();
        RuleFor(x => x.TokenLookupHash).NotEmpty();
        RuleFor(x => x.Salt).NotEmpty();
        RuleFor(x => x.CreatedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class ClusterExportValidator : AbstractValidator<ClusterExport>
{
    public ClusterExportValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.AccessNodes).NotNull().NotEmpty();
        RuleForEach(x => x.AccessNodes).SetValidator(new AccessNodeDtoValidator());
        RuleFor(x => x.BackupRestoreMaxDop).InclusiveBetween(1, 1024).When(x => x.BackupRestoreMaxDop is not null);
        RuleFor(x => x.CreatedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class AccessNodeDtoValidator : AbstractValidator<AccessNodeDto>
{
    public AccessNodeDtoValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Host).NotEmpty().MaximumLength(512);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
    }
}

public sealed class BackupTargetExportValidator : AbstractValidator<BackupTargetExport>
{
    public BackupTargetExportValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.S3).NotNull().SetValidator(new S3TargetSettingsDtoValidator());
        RuleFor(x => x.CreatedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class S3TargetSettingsDtoValidator : AbstractValidator<S3TargetSettingsDto>
{
    public S3TargetSettingsDtoValidator()
    {
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(2048);
        RuleFor(x => x.Region).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Bucket).NotEmpty().MaximumLength(255);
        RuleFor(x => x.PathPrefix).MaximumLength(1024).When(x => x.PathPrefix is not null);
    }
}

public sealed class BackupPolicyExportValidator : AbstractValidator<BackupPolicyExport>
{
    public BackupPolicyExportValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.SourceClusterId).NotEmpty();
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.SelectorJsonVersion).Equal(1);
        RuleFor(x => x.Selector).NotNull().SetValidator(new PolicySelectorValidator());
        RuleFor(x => x.Retention).SetValidator(new BackupRetentionDtoValidator()!).When(x => x.Retention is not null);
        RuleFor(x => x.FailedBackupRetentionMode).IsInEnum();
        RuleFor(x => x.CreatedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class BackupScheduleExportValidator : AbstractValidator<BackupScheduleExport>
{
    public BackupScheduleExportValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).RequiredName(160);
        RuleFor(x => x.PolicyId).NotEmpty();
        RuleFor(x => x.BackupType).IsInEnum();
        RuleFor(x => x.CronExpression).ValidQuartzCron();
        RuleFor(x => x.TimeZoneId).ValidTimeZone();
        RuleFor(x => x.MissedRunGracePeriod).GreaterThan(TimeSpan.Zero).When(x => x.MissedRunGracePeriod is not null);
        RuleFor(x => x.Description).MaximumLength(2048).When(x => x.Description is not null);
        RuleFor(x => x.CreatedAt).NotEqual(default(DateTimeOffset));
    }
}

public sealed class SeedMissingBackupOperationRequestValidator : AbstractValidator<SeedMissingBackupOperationRequest>
{
    public SeedMissingBackupOperationRequestValidator()
    {
        RuleFor(x => x.SourceClusterId).NotEmpty();
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.Database).NotEmpty().MaximumLength(512);
        RuleFor(x => x.Table).NotEmpty().MaximumLength(512);
        RuleFor(x => x.ShardCount).InclusiveBetween(1, 1024);
    }
}

internal static class ValidationRuleExtensions
{
    public static IRuleBuilderOptions<T, string> RequiredName<T>(this IRuleBuilder<T, string> rule, int maxLength) =>
        rule.NotEmpty().MaximumLength(maxLength).Must(x => x.Trim().Length > 0).WithMessage("Name is required.");

    public static IRuleBuilderOptions<T, string> ValidQuartzCron<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Must(value =>
        {
            try
            {
                QuartzCronProjection.ValidateExpression(value);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }).WithMessage("CronExpression is invalid.");

    public static IRuleBuilderOptions<T, string> ValidTimeZone<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Must(value => TimeZoneInfo.TryFindSystemTimeZoneById(value, out _)).WithMessage("TimeZoneId is invalid.");
}
