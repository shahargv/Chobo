import type {
  AccessTokenDto,
  ApplicationLogEntryDto,
  PagedResultDto,
  AuditEntryDto,
  BackupDto,
  BackupGarbageCollectorStatusDto,
  BackupMetadataRecoveryResult,
  BackupPolicyDto,
  BackupRunStatus,
  BackupScheduleDto,
  BackupTargetDto,
  ClickHouseClusterNamesDto,
  ClickHouseClusterTopologyDto,
  ClusterConnectionTestResult,
  ClusterDto,
  CreateAccessTokenRequest,
  CreateAccessTokenResponse,
  CreateUserRequest,
  CreateUserResponse,
  DashboardDto,
  ExportEnvelope,
  InitiateRestoreRequest,
  InstallRequest,
  InstallResponse,
  InstallStatusDto,
  ManualBackupRequest,
  PolicyEvaluationDto,
  PolicyEvaluationRequest,
  PolicyInventory,
  PolicySimulationDto,
  PolicySimulationRequest,
  RecoverBackupMetadataFromPathRequest,
  RecoverBackupMetadataScanRequest,
  RestoreDto,
  ServerVersionDto,
  SchemaBackupDto,
  SchemaBackupSummaryDto,
  StorageConnectionTestResult,
  UpsertClusterRequest,
  UpsertPolicyRequest,
  UpsertS3TargetRequest,
  UpsertScheduleRequest,
  UpdateClusterCredentialsRequest,
  ValidateScheduleCronRequest,
  ValidateScheduleCronResponse,
  UserDto
} from "./generated";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number
  ) {
    super(message);
  }
}

export class ChoboApiClient {
  constructor(
    private readonly getToken: () => string | null,
    private readonly onUnauthorized: () => void,
    private readonly baseUrl = ""
  ) {}

  serverVersion() { return this.get<ServerVersionDto>("server/version"); }
  installStatus() { return this.get<InstallStatusDto>("server/install/status"); }
  install(request: InstallRequest) { return this.post<InstallResponse>("server/install", request); }
  dashboard(nextHours = 6) { return this.get<DashboardDto>(`dashboard?nextHours=${nextHours}`); }
  metrics() { return this.get<Record<string, number | null>>("metrics"); }
  metricsJsonText() { return this.requestText("metrics"); }

  users() { return this.get<UserDto[]>("users"); }
  addUser(request: CreateUserRequest) { return this.post<CreateUserResponse>("users", request); }
  removeUser(id: string) { return this.deleteVoid(`users/${id}`); }
  tokens(userId: string) { return this.get<AccessTokenDto[]>(`users/${userId}/tokens`); }
  addToken(userId: string, request: CreateAccessTokenRequest) { return this.post<CreateAccessTokenResponse>(`users/${userId}/tokens`, request); }
  removeToken(userId: string, tokenId: string) { return this.deleteVoid(`users/${userId}/tokens/${tokenId}`); }

  clusters() { return this.get<ClusterDto[]>("clusters"); }
  addCluster(request: UpsertClusterRequest) { return this.post<ClusterDto>("clusters", request); }
  updateCluster(id: string, request: UpsertClusterRequest) { return this.put<ClusterDto>(`clusters/${id}`, request); }
  updateClusterCredentials(id: string, request: UpdateClusterCredentialsRequest) { return this.post<ClusterDto>(`clusters/${id}/credentials`, request); }
  removeCluster(id: string) { return this.deleteVoid(`clusters/${id}`); }
  testCluster(id: string) { return this.post<ClusterConnectionTestResult>(`clusters/${id}/test-connection`, {}); }
  clickHouseClusterNames(id: string) { return this.get<ClickHouseClusterNamesDto>(`clusters/${id}/clickhouse-cluster-names`); }
  clusterTopology(id: string) { return this.get<ClickHouseClusterTopologyDto>(`clusters/${id}/topology`); }

  targets() { return this.get<BackupTargetDto[]>("targets"); }
  addTarget(request: UpsertS3TargetRequest) { return this.post<BackupTargetDto>("targets/s3", request); }
  updateTarget(id: string, request: UpsertS3TargetRequest) { return this.put<BackupTargetDto>(`targets/${id}/s3`, request); }
  removeTarget(id: string) { return this.deleteVoid(`targets/${id}`); }
  testTarget(id: string) { return this.post<StorageConnectionTestResult>(`targets/${id}/test-connection`, {}); }

  policies() { return this.get<BackupPolicyDto[]>("policies"); }
  addPolicy(request: UpsertPolicyRequest) { return this.post<BackupPolicyDto>("policies", request); }
  updatePolicy(id: string, request: UpsertPolicyRequest) { return this.put<BackupPolicyDto>(`policies/${id}`, request); }
  removePolicy(id: string) { return this.deleteVoid(`policies/${id}`); }
  evaluatePolicy(id: string, request: PolicyEvaluationRequest) { return this.post<PolicyEvaluationDto>(`policies/${id}/evaluate`, request); }
  policyInventory(sourceClusterId: string) { return this.get<PolicyInventory>(`policies/inventory?sourceClusterId=${encodeURIComponent(sourceClusterId)}`); }
  simulatePolicy(request: PolicySimulationRequest) { return this.post<PolicySimulationDto>("policies/simulate", request); }

  schedules() { return this.get<BackupScheduleDto[]>("schedules"); }
  addSchedule(request: UpsertScheduleRequest) { return this.post<BackupScheduleDto>("schedules", request); }
  updateSchedule(id: string, request: UpsertScheduleRequest) { return this.put<BackupScheduleDto>(`schedules/${id}`, request); }
  removeSchedule(id: string) { return this.deleteVoid(`schedules/${id}`); }
  enableSchedule(id: string) { return this.post<void>(`schedules/${id}/enable`, {}); }
  disableSchedule(id: string) { return this.post<void>(`schedules/${id}/disable`, {}); }
  validateScheduleCron(request: ValidateScheduleCronRequest) { return this.post<ValidateScheduleCronResponse>("schedules/validate-cron", request); }

  backups(filters: { policyId?: string; clusterName?: string; tableName?: string; status?: BackupRunStatus } = {}) {
    return this.get<BackupDto[]>(`backups${query(filters)}`);
  }
  backup(id: string) { return this.get<BackupDto>(`backups/${id}`); }
  manualBackup(request: ManualBackupRequest) { return this.post<BackupDto>("backups/manual", request); }
  pinBackup(id: string) { return this.post<BackupDto>(`backups/${id}/pin`, {}); }
  unpinBackup(id: string) { return this.post<BackupDto>(`backups/${id}/unpin`, {}); }
  deleteBackup(id: string, force = false, confirmDestructive = false) { return this.delete<BackupDto>(`backups/${id}${query({ force: force ? true : undefined, confirmDestructive: confirmDestructive ? true : undefined })}`); }
  cancelBackup(id: string) { return this.post<BackupDto>(`backups/${id}/cancel`, {}); }
  recoverBackupFromPath(request: RecoverBackupMetadataFromPathRequest) { return this.post<BackupMetadataRecoveryResult>("backups/recover/from-path", request); }
  recoverBackupFromScan(request: RecoverBackupMetadataScanRequest) { return this.post<BackupMetadataRecoveryResult>("backups/recover/scan", request); }
  backupGarbageCollectorStatus() { return this.get<BackupGarbageCollectorStatusDto>("backups/garbage-collector/status"); }
  runBackupGarbageCollector() { return this.post<BackupGarbageCollectorStatusDto>("backups/garbage-collector/run", {}); }

  schemaBackups(filters: { from?: string; to?: string } = {}) { return this.get<SchemaBackupSummaryDto[]>(`schema/backups${query(filters)}`); }
  backupSchema(id: string) { return this.get<SchemaBackupDto>(`schema/backups/${id}`); }
  exportBackupSchema(id: string, database?: string) { return this.requestText(`schema/backups/${id}/export${query({ database })}`, { accept: "text/plain" }); }

  restores() { return this.get<RestoreDto[]>("restores"); }
  restore(id: string) { return this.get<RestoreDto>(`restores/${id}`); }
  initiateRestore(request: InitiateRestoreRequest) { return this.post<RestoreDto>("restores/initiate", request); }
  cancelRestore(id: string) { return this.post<RestoreDto>(`restores/${id}/cancel`, {}); }

  logs(params: { startTime?: string; endTime?: string; last?: number; offset?: number; limit?: number; operationId?: string } = {}) { return this.get<PagedResultDto<ApplicationLogEntryDto>>(`logs${query(params)}`); }
  clearLogs(before: string) { return this.post<{ deleted: number }>("logs/clear", { before }); }
  audits(params: { startTime?: string; endTime?: string; last?: number; offset?: number; limit?: number; operationId?: string } = {}) { return this.get<PagedResultDto<AuditEntryDto>>(`audit${query(params)}`); }
  clearAudits(before: string) { return this.post<{ deleted: number }>("audit/clear", { before }); }

  exportData() { return this.get<ExportEnvelope>("data/export"); }
  importData(envelope: ExportEnvelope) { return this.post<void>("data/import", envelope); }
  exportConfig() { return this.get<ExportEnvelope>("config/export"); }
  importConfig(envelope: ExportEnvelope) { return this.post<void>("config/import", envelope); }

  private get<T>(path: string) { return this.request<T>(path); }
  private requestText(path: string, options: { accept?: string } = {}) { return this.requestRaw(path, { accept: options.accept }).then((response) => response.text()); }
  private post<T>(path: string, body: unknown) { return this.request<T>(path, { method: "POST", body: JSON.stringify(body) }); }
  private put<T>(path: string, body: unknown) { return this.request<T>(path, { method: "PUT", body: JSON.stringify(body) }); }
  private delete<T>(path: string) { return this.request<T>(path, { method: "DELETE" }); }
  private async deleteVoid(path: string) { await this.request<void>(path, { method: "DELETE" }); }

  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const response = await this.requestRaw(path, { init });
    if (response.status === 204) return undefined as T;
    const text = await response.text();
    return text ? (JSON.parse(text) as T) : (undefined as T);
  }

  private async requestRaw(path: string, options: { init?: RequestInit; accept?: string } = {}): Promise<Response> {
    const init = options.init ?? {};
    const token = this.getToken();
    const headers = new Headers(init.headers);
    headers.set("Accept", options.accept ?? "application/json");
    if (init.body) headers.set("Content-Type", "application/json");
    if (token) headers.set("Authorization", `Bearer ${token}`);

    const response = await fetch(`${this.baseUrl}/api/v1/${path}`, { ...init, headers });
    if (response.status === 401) this.onUnauthorized();
    if (!response.ok) {
      const text = await response.text();
      let message = text || response.statusText;
      try {
        const parsed = JSON.parse(text) as { error?: string };
        message = parsed.error ?? message;
      } catch {
        // Keep text response.
      }
      throw new ApiError(message, response.status);
    }

    return response;
  }
}

function query(values: Record<string, string | number | boolean | undefined | null>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && `${value}` !== "") params.set(key, `${value}`);
  }
  const text = params.toString();
  return text ? `?${text}` : "";
}



