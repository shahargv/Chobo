/* Generated from Chobo OpenAPI. Regenerate with npm run generate:api. */

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
export interface PagedResultDto<T> { items: T[]; offset: number; limit: number; totalCount: number; }

export interface AccessNodeDto { id: string; host: string; port: number; useTls: boolean; }
export interface AccessTokenDto { id: string; userId: string; name: string; isActive: boolean; createdAt: string; deactivatedAt?: string | null; }
export interface AccessTokenExport { id: string; userId: string; name: string; tokenHash: string; tokenLookupHash: string; salt: string; isActive: boolean; createdAt: string; deactivatedAt?: string | null; }
export interface ApplicationLogEntryDto { id: number; timestamp: string; level: string; category: string; message: string; exception?: string | null; }
export interface ApplicationLogEntryDtoPagedResultDto { items: ApplicationLogEntryDto[]; offset: number; limit: number; totalCount: number; }
export interface AuditEntryDto { id: number; timestamp: string; actorUserId?: string | null; actorName: string; action: string; entityType: string; entityId: string; details: JsonValue; }
export interface AuditEntryDtoPagedResultDto { items: AuditEntryDto[]; offset: number; limit: number; totalCount: number; }
export type BackupContentMode = "SchemaAndData" | "SchemaOnly";
export interface BackupDto { id: string; triggerType: BackupTriggerType; status: BackupRunStatus; backupType: BackupType; contentMode: BackupContentMode; sourceClusterId: string; targetId: string; policyId?: string | null; scheduleId?: string | null; requestedByUserId?: string | null; requestedByName: string; manualRequestJson?: string | null; createdAt: string; startedAt?: string | null; endedAt?: string | null; error?: string | null; failureReason?: string | null; isPinned: boolean; pinnedAt?: string | null; pinnedByUserId?: string | null; pinnedByName?: string | null; deletionReason?: string | null; deletionRequestedAt?: string | null; deletionStartedAt?: string | null; deletedAt?: string | null; deletionError?: string | null; deletionAttemptCount: number; tableCount: number; backupSizeBytes: number; tables: BackupTableDto[]; }
export interface BackupExport { id: string; triggerType: BackupTriggerType; status: BackupRunStatus; backupType: BackupType; contentMode: BackupContentMode; sourceClusterId: string; targetId: string; policyId?: string | null; scheduleId?: string | null; manualRequestJson?: string | null; requestedByUserId?: string | null; requestedByName: string; createdAt: string; queuedAt: string; startedAt?: string | null; completedAt: string; error?: string | null; failureReason?: string | null; isPinned: boolean; pinnedAt?: string | null; pinnedByUserId?: string | null; pinnedByName?: string | null; deletionReason?: string | null; deletionRequestedAt?: string | null; deletionStartedAt?: string | null; deletedAt?: string | null; deletionError?: string | null; deletionAttemptCount: number; }
export interface BackupGarbageCollectorStatusDto { isRunning: boolean; currentRunReason?: string | null; lastStartedAt?: string | null; lastCompletedAt?: string | null; lastError?: string | null; lastMarkedCount: number; lastPendingCleanupCount: number; lastCleanedCount: number; lastFailedCount: number; }
export interface BackupMetadataRecoveryItem { backupId: string; status: BackupRunStatus; source: string; imported: boolean; updated: boolean; message: string; }
export interface BackupMetadataRecoveryResult { scannedManifestCount: number; importedBackupCount: number; updatedBackupCount: number; skippedManifestCount: number; items: BackupMetadataRecoveryItem[]; errors: string[]; }
export interface BackupPolicyDto { id: string; name: string; sourceClusterId: string; targetId: string; contentMode: BackupContentMode; selectorJsonVersion: number; selector: PolicySelector; retention: BackupRetentionDto; failedBackupRetentionMode: FailedBackupRetentionMode; isSystemDefault: boolean; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface BackupPolicyExport { id: string; name: string; sourceClusterId: string; targetId: string; contentMode: BackupContentMode; selectorJsonVersion: number; selector: PolicySelector; retention: BackupRetentionDto; failedBackupRetentionMode: FailedBackupRetentionMode; isSystemDefault: boolean; isDeleted: boolean; createdAt: string; updatedAt?: string | null; deletedAt?: string | null; }
export interface BackupRetentionDto { fullRetentionMinutes?: number | null; incrementalRetentionMinutes?: number | null; minBackupsToKeep: number; minFullBackupsToKeep: number; retentionMinutes?: number | null; }
export type BackupRunStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Canceled" | "ManualDeleteRequested" | "ManualDeleted" | "FailedBackupDeleteRequested" | "FailedBackupDeletedByGarbageCollector" | "BackupExpiredDeleteStarted" | "BackupExpiredDeleted";
export interface BackupScheduleDto { id: string; name: string; policyId: string; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; description?: string | null; isSystemDefault: boolean; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface BackupScheduleExport { id: string; name: string; policyId: string; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; description?: string | null; isSystemDefault: boolean; isDeleted: boolean; createdAt: string; updatedAt?: string | null; deletedAt?: string | null; }
export interface BackupTableDto { id: string; backupId: string; effectiveBackupType: BackupType; parentFullBackupId?: string | null; parentFullBackupTableId?: string | null; database: string; table: string; engine: string; dataBackedUp: boolean; schemaDefinitionId: string; s3Path: string; backupSizeBytes: number; status: BackupTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; shards: BackupTableShardDto[]; }
export interface BackupTableExport { id: string; backupId: string; effectiveBackupType: BackupType; parentFullBackupId?: string | null; parentFullBackupTableId?: string | null; database: string; table: string; engine: string; dataBackedUp: boolean; schemaDefinitionId: string; s3Path: string; backupSizeBytes: number; status: BackupTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; }
export interface BackupTableShardDto { id: string; backupTableId: string; effectiveBackupType: BackupType; parentFullBackupId?: string | null; parentFullBackupTableShardId?: string | null; sourceShardNumber: number; sourceShardName?: string | null; replicaNumber: number; host: string; port: number; useTls: boolean; s3Path: string; backupSizeBytes: number; status: BackupTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; }
export interface BackupTableShardExport { id: string; backupTableId: string; effectiveBackupType: BackupType; parentFullBackupId?: string | null; parentFullBackupTableShardId?: string | null; sourceShardNumber: number; sourceShardName?: string | null; replicaNumber: number; host: string; port: number; useTls: boolean; s3Path: string; backupSizeBytes: number; status: BackupTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; }
export type BackupTableStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Skipped";
export interface BackupTargetDto { id: string; name: string; type: BackupTargetType; s3: S3TargetSettingsDto; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface BackupTargetExport { id: string; name: string; type: BackupTargetType; s3: S3TargetSettingsDto; encryptedAccessKey?: string | null; encryptedAccessKeyKeyId?: string | null; encryptedSecretKey?: string | null; encryptedSecretKeyKeyId?: string | null; isDeleted: boolean; createdAt: string; updatedAt?: string | null; deletedAt?: string | null; }
export type BackupTargetType = "S3";
export type BackupTriggerType = "Manual" | "Scheduled";
export type BackupType = "Full" | "Incremental";
export interface ClearApplicationLogsRequest { before: string; }
export interface ClearAuditEntriesRequest { before: string; }
export interface ClickHouseClusterNamesDto { clusterId: string; names: string[]; }
export interface ClickHouseClusterShardDto { shardNumber: number; shardName: string; replicaNumber: number; host: string; port: number; useTls: boolean; errorsCount: number; }
export interface ClickHouseClusterTopologyDto { clusterId: string; shards: ClickHouseClusterShardDto[]; }
export interface ClusterConnectionTestResult { clusterId: string; succeeded: boolean; message: string; }
export interface ClusterDto { id: string; name: string; mode: ClusterMode; accessNodes: AccessNodeDto[]; backupRestoreMaxDop?: number | null; clickHouseClusterName?: string | null; isDeleted: boolean; createdAt: string; updatedAt?: string | null; }
export interface ClusterExport { id: string; name: string; mode: ClusterMode; clickHouseClusterName?: string | null; accessNodes: AccessNodeDto[]; encryptedUserName?: string | null; encryptedUserNameKeyId?: string | null; encryptedPassword?: string | null; encryptedPasswordKeyId?: string | null; backupRestoreMaxDop?: number | null; isDeleted: boolean; createdAt: string; updatedAt?: string | null; deletedAt?: string | null; }
export type ClusterMode = "SingleInstance" | "Cluster";
export interface CreateAccessTokenRequest { name: string; }
export interface CreateAccessTokenResponse { tokenId: string; userId: string; name: string; accessToken: string; }
export interface CreateUserRequest { userName?: string | null; }
export interface CreateUserResponse { userId: string; userName?: string | null; accessToken: string; }
export interface DashboardDto { generatedAt: string; futureWindowHours: number; runningBackups: DashboardRunningBackupDto[]; schedules: DashboardScheduleDto[]; futureSchedules: DashboardFutureScheduleDto[]; }
export interface DashboardFutureScheduleDto { scheduleId: string; scheduleName?: string | null; policyId: string; policyName?: string | null; backupType: BackupType; plannedRunAt: string; timeZoneId: string; }
export interface DashboardRunningBackupDto { backupId: string; status: BackupRunStatus; triggerType: BackupTriggerType; policyId?: string | null; policyName?: string | null; scheduleId?: string | null; scheduleName?: string | null; createdAt: string; startedAt?: string | null; failureReason?: string | null; isPinned: boolean; deletionRequestedAt?: string | null; deletionReason?: string | null; tableCount: number; shardCount: number; succeededShardCount: number; failedShardCount: number; runningShardCount: number; }
export interface DashboardScheduleDto { scheduleId: string; scheduleName?: string | null; policyId: string; policyName?: string | null; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; lastRunAt?: string | null; lastRunStatus: BackupRunStatus; lastRunFailureReason?: string | null; lastRunIsPinned: boolean; lastRunDeletionRequestedAt: string; lastSuccessfulRunCompletedAt?: string | null; }
export interface ExportEnvelope { exportVersion: number; schemaVersion: number; generatedAt: string; productVersion: string; data: ExportPayload; }
export interface ExportPayload { users: UserExport[]; accessTokens: AccessTokenExport[]; clusters: ClusterExport[]; backupTargets: BackupTargetExport[]; backupPolicies: BackupPolicyExport[]; backupSchedules: BackupScheduleExport[]; schemaDefinitions: SchemaDefinitionExport[]; backups: BackupExport[]; backupTables: BackupTableExport[]; backupTableShards: BackupTableShardExport[]; restores: RestoreExport[]; restoreTables: RestoreTableExport[]; restoreTableShards: RestoreTableShardExport[]; }
export type FailedBackupRetentionMode = "KeepAndExcludeFromMinBackupsToKeep" | "DeleteByGarbageCollectorAfterFailure";
export interface InitiateRestoreRequest { backupId: string; targetClusterId: string; database?: string; table?: string; targetDatabase?: string | null; targetTable?: string | null; append: boolean; allowSchemaMismatch: boolean; layout: RestoreLayout; sourceShard?: number | null; targetShard?: number | null; tables?: RestoreTableMappingRequest[]; schemaOnly: boolean; sourceShards?: number[] | null; targetShards?: number[] | null; confirmDestructive: boolean; }
export interface InstallRequest { adminUser: string; }
export interface InstallResponse { userId: string; userName?: string | null; accessToken: string; }
export interface InstallStatusDto { requiresInstallation: boolean; message: string; }
export interface ManualBackupRequest { clusterId: string; targetId: string; selector: PolicySelector; backupType: BackupType; policyId?: string | null; schemaOnly: boolean; }
export interface PolicyEvaluationDto { policyId: string; policyName?: string | null; sourceClusterId: string; selectorJsonVersion: number; selector: PolicySelector; tables: PolicyInventoryTable[]; }
export interface PolicyEvaluationRequest { inventory: PolicyInventory; }
export interface PolicyInventory { tables: PolicyInventoryTable[]; }
export interface PolicyInventoryTable { database: string; table: string; }
export type PolicyMatchKind = "All" | "Exact" | "Wildcard";
export interface PolicySelector { version: number; rules: PolicySelectorRule[]; }
export type PolicySelectorAction = "Include" | "Exclude";
export interface PolicySelectorRule { action: PolicySelectorAction; database: SelectorPattern; table: SelectorPattern; }
export interface PolicySimulationDto { sourceClusterId: string; selector: PolicySelector; inventory: PolicyInventoryTable[]; tables: PolicyInventoryTable[]; }
export interface PolicySimulationRequest { sourceClusterId: string; selector: PolicySelector; }
export interface RecoverBackupMetadataFromPathRequest { targetId: string; backupPath?: string | null; }
export interface RecoverBackupMetadataScanRequest { targetId: string; scanRoot?: string | null; }
export interface RestoreDto { id: string; backupId: string; targetClusterId: string; status: RestoreRunStatus; append: boolean; allowSchemaMismatch: boolean; layout: RestoreLayout; sourceShard?: number | null; targetShard?: number | null; requestedByUserId?: string | null; requestedByName: string; requestJson: string; createdAt: string; startedAt?: string | null; endedAt?: string | null; error?: string | null; failureReason?: string | null; tables: RestoreTableDto[]; }
export interface RestoreExport { id: string; backupId: string; targetClusterId: string; status: RestoreRunStatus; append: boolean; allowSchemaMismatch: boolean; layout: RestoreLayout; sourceShard?: number | null; targetShard?: number | null; requestJson: string; requestedByUserId?: string | null; requestedByName: string; createdAt: string; queuedAt: string; startedAt?: string | null; completedAt: string; error?: string | null; failureReason?: string | null; }
export type RestoreLayout = "Preserve" | "SingleNode" | "Redistribute";
export type RestoreRunStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Canceled";
export interface RestoreTableDto { id: string; restoreId: string; backupTableId: string; sourceDatabase: string; sourceTable: string; targetDatabase?: string | null; targetTable?: string | null; append: boolean; allowSchemaMismatch: boolean; schemaOnly: boolean; status: RestoreTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; warning?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; shards: RestoreTableShardDto[]; }
export interface RestoreTableExport { id: string; restoreId: string; backupTableId: string; sourceDatabase: string; sourceTable: string; targetDatabase?: string | null; targetTable?: string | null; append: boolean; allowSchemaMismatch: boolean; schemaOnly: boolean; status: RestoreTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; warning?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; }
export interface RestoreTableMappingRequest { backupTableId: string; targetDatabase?: string | null; targetTable?: string | null; append?: boolean | null; allowSchemaMismatch?: boolean | null; schemaOnly?: boolean | null; }
export interface RestoreTableShardDto { id: string; restoreTableId: string; backupTableShardId: string; sourceShardNumber: number; targetShardNumber?: number | null; targetShardName?: string | null; targetReplicaNumber?: number | null; targetHost: string; targetPort: number; targetUseTls: boolean; layoutRole: string; restoreDatabase: string; restoreTableName: string; status: RestoreTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; warning?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; }
export interface RestoreTableShardExport { id: string; restoreTableId: string; backupTableShardId: string; sourceShardNumber: number; targetShardNumber?: number | null; targetShardName?: string | null; targetReplicaNumber?: number | null; targetHost: string; targetPort: number; targetUseTls: boolean; layoutRole: string; restoreDatabase: string; restoreTableName: string; status: RestoreTableStatus; clickHouseOperationId?: string | null; clickHouseStatus?: string | null; warning?: string | null; startedAt?: string | null; completedAt: string; error?: string | null; }
export type RestoreTableStatus = "Queued" | "Running" | "Succeeded" | "PartiallySucceeded" | "Failed" | "Skipped";
export interface S3TargetSettingsDto { endpoint: string; region: string; bucket: string; pathPrefix?: string | null; forcePathStyle: boolean; }
export interface SchemaBackupDto { backupId: string; status: BackupRunStatus; contentMode: BackupContentMode; databases: SchemaDatabaseDto[]; }
export interface SchemaBackupSummaryDto { id: string; status: BackupRunStatus; contentMode: BackupContentMode; backupType: BackupType; sourceClusterId: string; sourceClusterName: string; policyId?: string | null; createdAt: string; endedAt?: string | null; tableCount: number; }
export interface SchemaDatabaseDto { database: string; tables: SchemaTableDto[]; }
export interface SchemaDefinitionExport { id: string; schemaHash: string; database: string; table: string; engine: string; createTableSql: string; columnsJson: string; createdAt: string; }
export interface SchemaTableDto { backupTableId: string; database: string; table: string; engine: string; dataBackedUp: boolean; createTableSql: string; columnsJson: string; }
export interface SelectorPattern { kind: PolicyMatchKind; value: string; }
export interface ServerVersionDto { productName: string; productVersion: string; apiVersion: number; schemaVersion: number; databaseSchemaVersion: number; }
export interface StorageConnectionTestResult { targetId: string; targetType: BackupTargetType; succeeded: boolean; message: string; }
export interface UpdateClusterCredentialsRequest { userName?: string | null; password?: string | null; }
export interface UpsertAccessNodeRequest { host: string; port: number; useTls: boolean; }
export interface UpsertClusterRequest { name: string; mode: ClusterMode; accessNodes: UpsertAccessNodeRequest[]; userName?: string | null; password?: string | null; backupRestoreMaxDop?: number | null; clickHouseClusterName?: string | null; }
export interface UpsertPolicyRequest { name: string; sourceClusterId: string; targetId: string; selector: PolicySelector; contentMode: BackupContentMode; retention: BackupRetentionDto; failedBackupRetentionMode: FailedBackupRetentionMode; }
export interface UpsertS3TargetRequest { name: string; endpoint: string; region: string; bucket: string; pathPrefix?: string | null; forcePathStyle: boolean; accessKey?: string | null; secretKey?: string | null; }
export interface UpsertScheduleRequest { name: string; policyId: string; backupType: BackupType; cronExpression: string; timeZoneId: string; isEnabled: boolean; missedRunGracePeriod?: string | null; description?: string | null; }
export interface UserDto { id: string; userName?: string | null; isActive: boolean; createdAt: string; deactivatedAt?: string | null; }
export interface UserExport { id: string; userName?: string | null; isActive: boolean; createdAt: string; deactivatedAt?: string | null; }
export interface ValidateScheduleCronRequest { cronExpression: string; timeZoneId: string; }
export interface ValidateScheduleCronResponse { isValid: boolean; error?: string | null; nextRuns: string[]; }
export const openApiSchemaNames = [
  "AccessNodeDto",
  "AccessTokenDto",
  "AccessTokenExport",
  "ApplicationLogEntryDto",
  "ApplicationLogEntryDtoPagedResultDto",
  "AuditEntryDto",
  "AuditEntryDtoPagedResultDto",
  "BackupContentMode",
  "BackupDto",
  "BackupExport",
  "BackupGarbageCollectorStatusDto",
  "BackupMetadataRecoveryItem",
  "BackupMetadataRecoveryResult",
  "BackupPolicyDto",
  "BackupPolicyExport",
  "BackupRetentionDto",
  "BackupRunStatus",
  "BackupScheduleDto",
  "BackupScheduleExport",
  "BackupTableDto",
  "BackupTableExport",
  "BackupTableShardDto",
  "BackupTableShardExport",
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
  "InstallRequest",
  "InstallResponse",
  "InstallStatusDto",
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
  "RestoreExport",
  "RestoreLayout",
  "RestoreRunStatus",
  "RestoreTableDto",
  "RestoreTableExport",
  "RestoreTableMappingRequest",
  "RestoreTableShardDto",
  "RestoreTableShardExport",
  "RestoreTableStatus",
  "S3TargetSettingsDto",
  "SchemaBackupDto",
  "SchemaBackupSummaryDto",
  "SchemaDatabaseDto",
  "SchemaDefinitionExport",
  "SchemaTableDto",
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
