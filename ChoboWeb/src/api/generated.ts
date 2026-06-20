/* Generated from Chobo OpenAPI. Regenerate with npm run generate:api. */

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

export type BackupRunStatus =
  | "Queued"
  | "Running"
  | "Succeeded"
  | "PartiallySucceeded"
  | "Failed"
  | "Canceled"
  | "ManualDeleteRequested"
  | "ManualDeleted"
  | "FailedBackupDeleteRequested"
  | "FailedBackupDeletedByGarbageCollector"
  | "BackupExpiredDeleteStarted"
  | "BackupExpiredDeleted";

export type BackupTableStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Skipped";
export type BackupTriggerType = "Manual" | "Scheduled";
export type RestoreRunStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Canceled";
export type RestoreTableStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Skipped";
export type RestoreLayout = "Preserve" | "SingleNode" | "Redistribute";
export type BackupType = "Full" | "Incremental";
export type ClusterMode = "SingleInstance" | "Cluster";
export type BackupTargetType = "S3";
export type PolicyMatchKind = "All" | "Exact" | "Wildcard";
export type PolicySelectorAction = "Include" | "Exclude";
export type FailedBackupRetentionMode = "KeepAndExcludeFromMinBackupsToKeep" | "DeleteByGarbageCollectorAfterFailure";

export interface ErrorResponse { error: string; }
export interface ServerVersionDto { productName: string; productVersion: string; apiVersion: number; schemaVersion: number; databaseSchemaVersion: number; }
export interface PagedResultDto<T> { items: T[]; offset: number; limit: number; totalCount: number; }
export interface ApplicationLogEntryDto { id: number; timestamp: string; level: string; category: string; message: string; exception?: string | null; }
export interface ClearApplicationLogsRequest { before: string; }
export interface AuditEntryDto { id: number; timestamp: string; actorUserId?: string | null; actorName: string; action: string; entityType: string; entityId?: string | null; details: JsonValue; }
export interface ClearAuditEntriesRequest { before: string; }

export interface AccessNodeDto { id: string; host: string; port: number; useTls: boolean; }
export interface UpsertAccessNodeRequest { host: string; port: number; useTls: boolean; }
export interface ClusterDto { id: string; name: string; mode: ClusterMode; accessNodes: AccessNodeDto[]; backupRestoreMaxDop?: number | null; clickHouseClusterName?: string | null; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface UpsertClusterRequest { name: string; mode: ClusterMode; accessNodes: UpsertAccessNodeRequest[]; userName?: string | null; password?: string | null; backupRestoreMaxDop?: number | null; clickHouseClusterName?: string | null; }
export interface UpdateClusterCredentialsRequest { userName?: string | null; password?: string | null; }
export interface ClusterConnectionTestResult { clusterId: string; succeeded: boolean; message: string; }
export interface ClickHouseClusterNamesDto { clusterId: string; names: string[]; }
export interface ClickHouseClusterTopologyDto { clusterId: string; shards: ClickHouseClusterShardDto[]; }
export interface ClickHouseClusterShardDto { shardNumber: number; shardName?: string | null; replicaNumber: number; host: string; port: number; useTls: boolean; errorsCount: number; }

export interface S3TargetSettingsDto { endpoint: string; region: string; bucket: string; pathPrefix?: string | null; forcePathStyle: boolean; }
export interface BackupTargetDto { id: string; name: string; type: BackupTargetType; s3: S3TargetSettingsDto; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface UpsertS3TargetRequest { name: string; endpoint: string; region: string; bucket: string; pathPrefix?: string | null; forcePathStyle: boolean; accessKey?: string | null; secretKey?: string | null; }
export interface StorageConnectionTestResult { targetId: string; targetType: BackupTargetType; succeeded: boolean; message: string; }

export interface SelectorPattern { kind: PolicyMatchKind; value: string; }
export interface PolicySelectorRule { action: PolicySelectorAction; database: SelectorPattern; table: SelectorPattern; }
export interface PolicySelector { version: number; rules: PolicySelectorRule[]; }
export interface BackupRetentionDto { fullRetentionMinutes?: number | null; incrementalRetentionMinutes?: number | null; minBackupsToKeep: number; minFullBackupsToKeep: number; }
export interface BackupPolicyDto { id: string; name: string; sourceClusterId: string; targetId: string; selectorJsonVersion: number; selector: PolicySelector; retention?: BackupRetentionDto | null; failedBackupRetentionMode: FailedBackupRetentionMode; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface UpsertPolicyRequest { name: string; sourceClusterId: string; targetId: string; selector: PolicySelector; retention?: BackupRetentionDto | null; failedBackupRetentionMode: FailedBackupRetentionMode; }
export interface PolicyInventoryTable { database: string; table: string; }
export interface PolicyInventory { tables: PolicyInventoryTable[]; }
export interface PolicyEvaluationRequest { inventory: PolicyInventory; }
export interface PolicyEvaluationDto { policyId: string; policyName: string; sourceClusterId: string; selectorJsonVersion: number; selector: PolicySelector; tables: PolicyInventoryTable[]; }
export interface PolicySimulationRequest { sourceClusterId: string; selector: PolicySelector; }
export interface PolicySimulationDto { sourceClusterId: string; selector: PolicySelector; inventory: PolicyInventoryTable[]; tables: PolicyInventoryTable[]; }

export interface BackupScheduleDto { id: string; name: string; policyId: string; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; description?: string | null; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface UpsertScheduleRequest { name: string; policyId: string; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; description?: string | null; }
export interface ValidateScheduleCronRequest { cronExpression: string; timeZoneId: string; }
export interface ValidateScheduleCronResponse { isValid: boolean; error?: string | null; nextRuns: string[]; }

export interface BackupDto { id: string; triggerType: BackupTriggerType; status: BackupRunStatus; backupType: BackupType; sourceClusterId: string; targetId: string; policyId?: string | null; scheduleId?: string | null; requestedByUserId?: string | null; requestedByName: string; manualRequestJson?: string | null; createdAt: string; startedAt?: string | null; endedAt?: string | null; error?: string | null; failureReason?: string | null; isPinned: boolean; pinnedAt?: string | null; pinnedByUserId?: string | null; pinnedByName?: string | null; deletionReason?: string | null; deletionRequestedAt?: string | null; deletionStartedAt?: string | null; deletedAt?: string | null; deletionError?: string | null; deletionAttemptCount: number; tables: BackupTableDto[]; }
export interface BackupTableDto { id: string; backupId: string; effectiveBackupType: BackupType; parentFullBackupId?: string | null; parentFullBackupTableId?: string | null; database: string; table: string; engine: string; dataBackedUp: boolean; schemaDefinitionId: string; s3Path: string; status: BackupTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; startedAt?: string | null; completedAt?: string | null; error?: string | null; shards: BackupTableShardDto[]; }
export interface BackupTableShardDto { id: string; backupTableId: string; effectiveBackupType: BackupType; parentFullBackupId?: string | null; parentFullBackupTableShardId?: string | null; sourceShardNumber: number; sourceShardName?: string | null; replicaNumber: number; host: string; port: number; useTls: boolean; s3Path: string; status: BackupTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; startedAt?: string | null; completedAt?: string | null; error?: string | null; }
export interface ManualBackupRequest { clusterId: string; targetId: string; selector: PolicySelector; backupType: BackupType; policyId?: string | null; schemaOnly: boolean; }
export interface RecoverBackupMetadataFromPathRequest { targetId: string; backupPath: string; }
export interface RecoverBackupMetadataScanRequest { targetId: string; scanRoot: string; }
export interface BackupMetadataRecoveryResult { scannedManifestCount: number; importedBackupCount: number; updatedBackupCount: number; skippedManifestCount: number; items: BackupMetadataRecoveryItem[]; errors: string[]; }
export interface BackupMetadataRecoveryItem { backupId: string; status: BackupRunStatus; source: string; imported: boolean; updated: boolean; message: string; }

export interface RestoreDto { id: string; backupId: string; targetClusterId: string; status: RestoreRunStatus; append: boolean; allowSchemaMismatch: boolean; layout: RestoreLayout; sourceShard?: number | null; targetShard?: number | null; requestedByUserId?: string | null; requestedByName: string; requestJson: string; createdAt: string; startedAt?: string | null; endedAt?: string | null; error?: string | null; failureReason?: string | null; tables: RestoreTableDto[]; }
export interface RestoreTableDto { id: string; restoreId: string; backupTableId: string; sourceDatabase: string; sourceTable: string; targetDatabase: string; targetTable: string; append: boolean; allowSchemaMismatch: boolean; schemaOnly: boolean; status: RestoreTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; warning?: string | null; startedAt?: string | null; completedAt?: string | null; error?: string | null; shards: RestoreTableShardDto[]; }
export interface RestoreTableShardDto { id: string; restoreTableId: string; backupTableShardId: string; sourceShardNumber: number; targetShardNumber?: number | null; targetShardName?: string | null; targetReplicaNumber?: number | null; targetHost: string; targetPort: number; targetUseTls: boolean; layoutRole: string; restoreDatabase: string; restoreTableName: string; status: RestoreTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; warning?: string | null; startedAt?: string | null; completedAt?: string | null; error?: string | null; }
export interface InitiateRestoreRequest { backupId: string; targetClusterId: string; database?: string | null; table?: string | null; targetDatabase?: string | null; targetTable?: string | null; append: boolean; allowSchemaMismatch: boolean; layout?: RestoreLayout | null; sourceShard?: number | null; targetShard?: number | null; tables?: RestoreTableMappingRequest[] | null; schemaOnly: boolean; sourceShards?: number[] | null; targetShards?: number[] | null; }
export interface RestoreTableMappingRequest { backupTableId: string; targetDatabase?: string | null; targetTable?: string | null; append?: boolean | null; allowSchemaMismatch?: boolean | null; schemaOnly?: boolean | null; }

export interface DashboardDto { generatedAt: string; futureWindowHours: number; runningBackups: DashboardRunningBackupDto[]; schedules: DashboardScheduleDto[]; futureSchedules: DashboardFutureScheduleDto[]; }
export interface DashboardRunningBackupDto { backupId: string; status: BackupRunStatus; triggerType: BackupTriggerType; policyId?: string | null; policyName?: string | null; scheduleId?: string | null; scheduleName?: string | null; createdAt: string; startedAt?: string | null; failureReason?: string | null; isPinned: boolean; deletionRequestedAt?: string | null; deletionReason?: string | null; tableCount: number; shardCount: number; succeededShardCount: number; failedShardCount: number; runningShardCount: number; }
export interface DashboardScheduleDto { scheduleId: string; scheduleName: string; policyId: string; policyName?: string | null; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; lastRunAt?: string | null; lastRunStatus?: BackupRunStatus | null; lastRunFailureReason?: string | null; lastRunIsPinned: boolean; lastRunDeletionRequestedAt?: string | null; lastSuccessfulRunCompletedAt?: string | null; }
export interface DashboardFutureScheduleDto { scheduleId: string; scheduleName: string; policyId: string; policyName?: string | null; backupType: BackupType; plannedRunAt: string; timeZoneId: string; }

export interface UserDto { id: string; userName: string; isActive: boolean; createdAt: string; deactivatedAt?: string | null; }
export interface CreateUserRequest { userName: string; }
export interface CreateUserResponse { userId: string; userName: string; accessToken: string; }
export interface AccessTokenDto { id: string; userId: string; name: string; isActive: boolean; createdAt: string; deactivatedAt?: string | null; }
export interface CreateAccessTokenRequest { name: string; }
export interface CreateAccessTokenResponse { tokenId: string; userId: string; name: string; accessToken: string; }

export interface ExportEnvelope { exportVersion: number; schemaVersion: number; generatedAt: string; productVersion: string; data: JsonValue; }
export const openApiSchemaNames = [
  "AccessNodeDto",
  "AccessTokenDto",
  "AccessTokenExport",
  "ApplicationLogEntryDto",
  "AuditEntryDto",
  "BackupDto",
  "BackupMetadataRecoveryItem",
  "BackupMetadataRecoveryResult",
  "BackupPolicyDto",
  "BackupPolicyExport",
  "BackupRetentionDto",
  "BackupRunStatus",
  "BackupScheduleDto",
  "BackupScheduleExport",
  "BackupTableDto",
  "BackupTableShardDto",
  "BackupTableStatus",
  "BackupTargetDto",
  "BackupTargetExport",
  "BackupTargetType",
  "BackupTriggerType",
  "BackupType",
  "ClearApplicationLogsRequest",
  "ClearAuditEntriesRequest",
  "ClickHouseClusterNamesDto",
  "ClickHouseClusterShardDto",
  "ClickHouseClusterTopologyDto",
  "ClusterConnectionTestResult",
  "ClusterDto",
  "ClusterExport",
  "ClusterMode",
  "CreateAccessTokenRequest",
  "CreateAccessTokenResponse",
  "CreateUserRequest",
  "CreateUserResponse",
  "DashboardDto",
  "DashboardFutureScheduleDto",
  "DashboardRunningBackupDto",
  "DashboardScheduleDto",
  "ExportEnvelope",
  "ExportPayload",
  "FailedBackupRetentionMode",
  "InitiateRestoreRequest",
  "ManualBackupRequest",
  "PolicyEvaluationDto",
  "PolicyEvaluationRequest",
  "PolicyInventory",
  "PolicyInventoryTable",
  "PolicyMatchKind",
  "PolicySelector",
  "PolicySelectorAction",
  "PolicySelectorRule",
  "PolicySimulationDto",
  "PolicySimulationRequest",
  "RecoverBackupMetadataFromPathRequest",
  "RecoverBackupMetadataScanRequest",
  "RestoreDto",
  "RestoreLayout",
  "RestoreRunStatus",
  "RestoreTableDto",
  "RestoreTableMappingRequest",
  "RestoreTableShardDto",
  "RestoreTableStatus",
  "S3TargetSettingsDto",
  "SeedMissingBackupOperationRequest",
  "SelectorPattern",
  "ServerVersionDto",
  "StorageConnectionTestResult",
  "UpdateClusterCredentialsRequest",
  "UpsertAccessNodeRequest",
  "UpsertClusterRequest",
  "UpsertPolicyRequest",
  "UpsertS3TargetRequest",
  "UpsertScheduleRequest",
  "UserDto",
  "UserExport",
  "ValidateScheduleCronRequest",
  "ValidateScheduleCronResponse"
] as const;
