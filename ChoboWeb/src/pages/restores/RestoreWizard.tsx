import { useEffect, useMemo, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, RotateCcw } from "lucide-react";
import type { EntityRestorePlanRequest, InitiateRestoreRequest, SchemaTableDto } from "../../api/generated";
import { useApi } from "../../api-context";
import { ConfirmDialog, Page } from "../../components/ui";
import { ClickHouseAdvancedSettingsEditor, type ClickHouseSettings } from "../../components/ClickHouseAdvancedSettingsEditor";
import { BackupDrawer } from "../BackupsPage";
import type { RestoreMappingDraft, RestoreStep } from "./restoreTypes";
import { BackupChoiceStep, DestinationStep, ImpactSummary, RestoreStepper, ReviewStep, ScopeStep } from "./RestoreWizardSteps";
import { getMissingPreserveTargetShards, getRequestedBackupId, getSourceShardOptions, getTargetShardOptions, isBackupRestorable, restoreTargetTableName, validateRestoreRequest, validateStep } from "./restoreUtils";

export function RestoreWizard() {
  const { api, showToast } = useApi();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const backups = useQuery({ queryKey: ["backups", "restore"], queryFn: () => api.backups({}, { includeTables: true }) });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const policies = useQuery({ queryKey: ["policies"], queryFn: () => api.policies() });
  const [step, setStep] = useState<RestoreStep>(0);
  const [backupDateFilter, setBackupDateFilter] = useState(() => backupDateFromHours(72));
  const [activeBackupWindowHours, setActiveBackupWindowHours] = useState<number | null>(72);
  const [request, setRequest] = useState<InitiateRestoreRequest>({ backupId: "", targetClusterId: "", append: false, allowSchemaMismatch: false, layout: "Preserve", schemaOnly: false, confirmDestructive: false, clickHouseRestoreSettings: {} });
  const [mappings, setMappings] = useState<RestoreMappingDraft[]>([]);
  const [selectedSourceShards, setSelectedSourceShards] = useState<number[]>([]);
  const [selectedTargetShards, setSelectedTargetShards] = useState<number[]>([]);
  const [showConfirm, setShowConfirm] = useState(false);
  const [selectedBackupDetailId, setSelectedBackupDetailId] = useState<string | null>(null);
  const [clickHouseSettings, setClickHouseSettings] = useState<ClickHouseSettings>({});
  const [restoreToDate, setRestoreToDate] = useState("");
  const [settingsValid, setSettingsValid] = useState(true);
  const requestedBackupId = getRequestedBackupId(location.state);
  const isRedistribute = (request.layout ?? "Preserve") === "Redistribute";
  const settingsPreview = useQuery({
    queryKey: ["restore-settings-preview", request.backupId, request.targetClusterId],
    queryFn: () => api.restoreSettingsPreview({ backupId: request.backupId, targetClusterId: request.targetClusterId }),
    enabled: Boolean(request.backupId && request.targetClusterId),
    retry: false
  });
  const backupSchema = useQuery({
    queryKey: ["restore-schema", request.backupId],
    queryFn: () => api.backupSchema(request.backupId),
    enabled: Boolean(request.backupId)
  });
  const targetTopology = useQuery({
    queryKey: ["cluster-topology", request.targetClusterId],
    queryFn: () => api.clusterTopology(request.targetClusterId),
    enabled: Boolean(request.targetClusterId)
  });
  const clusterById = useMemo(() => new Map((clusters.data ?? []).map((cluster) => [cluster.id, cluster])), [clusters.data]);
  const policyById = useMemo(() => new Map((policies.data ?? []).map((policy) => [policy.id, policy])), [policies.data]);
  const restorableBackups = useMemo(() => (backups.data ?? []).filter(isBackupRestorable).filter((backup) => backupMatchesDateFilter(backup.createdAt, backupDateFilter)), [backups.data, backupDateFilter]);
  const selectedBackup = restorableBackups.find((backup) => backup.id === request.backupId) ?? null;
  const sourceShardOptions = useMemo(() => getSourceShardOptions(selectedBackup), [selectedBackup]);
  const targetShardOptions = useMemo(() => getTargetShardOptions(targetTopology.data?.shards), [targetTopology.data]);
  const schemaByTableId = useMemo(() => {
    const tables = backupSchema.data?.databases.flatMap((database) => database.tables) ?? [];
    return new Map<string, SchemaTableDto>(tables.map((table) => [table.backupTableId, table]));
  }, [backupSchema.data]);
  const isDifferentCluster = Boolean(selectedBackup && request.targetClusterId && selectedBackup.sourceClusterId !== request.targetClusterId);
  const preserveMissingTargetShards = isDifferentCluster && targetTopology.isSuccess ? getMissingPreserveTargetShards(selectedSourceShards, targetShardOptions) : [];
  const preserveShardCountMismatch = isDifferentCluster && targetTopology.isSuccess && selectedSourceShards.length === sourceShardOptions.length && sourceShardOptions.length > 0 && targetShardOptions.length > 0 && sourceShardOptions.length !== targetShardOptions.length;
  const preserveLayoutError = isDifferentCluster && targetTopology.isFetching
    ? "Preserve layout is checking target topology."
    : preserveMissingTargetShards.length > 0 ? `Preserve layout needs target shard${preserveMissingTargetShards.length === 1 ? "" : "s"} ${preserveMissingTargetShards.join(", ")}. Choose redistribute for this target cluster.`
      : preserveShardCountMismatch ? `Preserve layout requires matching source and target shard counts. Source has ${sourceShardOptions.length}; target has ${targetShardOptions.length}. Choose redistribute for this target cluster.` : null;
  const preserveLayoutDisabled = Boolean(preserveLayoutError);
  const preserveLayoutReason = preserveLayoutError;
  const selectedMappings = mappings.filter((mapping) => mapping.selected);
  const restoreErrors = validateRestoreRequest(request, mappings, selectedSourceShards, sourceShardOptions.length, selectedTargetShards, targetShardOptions.length, preserveLayoutError);
  const stepErrors = validateStep(step, request, mappings, selectedSourceShards, sourceShardOptions.length, selectedTargetShards, targetShardOptions.length, preserveLayoutError);
  const settingsPreviewBlocked = step === 3 && (settingsPreview.isLoading || settingsPreview.isError);

  useEffect(() => {
    if (!requestedBackupId || request.backupId || !restorableBackups.some((backup) => backup.id === requestedBackupId)) return;
    setRequest((current) => ({ ...current, backupId: requestedBackupId }));

    setStep(1);
  }, [request.backupId, requestedBackupId, restorableBackups]);

  useEffect(() => { setClickHouseSettings((settingsPreview.data?.settings ?? {}) as ClickHouseSettings); }, [settingsPreview.data, request.backupId, request.targetClusterId]);

  useEffect(() => {
    if (!selectedBackup) {
      setMappings([]);
      setSelectedSourceShards([]);
      return;
    }

    setMappings(selectedBackup.tables.map((table) => ({
      backupTableId: table.id,
      targetDatabase: table.database,
      targetTable: restoreTargetTableName(table.table),
      append: false,
      allowSchemaMismatch: false,
      schemaOnly: !table.dataBackedUp,
      createTableSqlOverride: null,
      shardSources: [],
      selected: false
    })));
    setSelectedSourceShards(getSourceShardOptions(selectedBackup).map((shard) => shard.value));
  }, [selectedBackup?.id]);

  useEffect(() => {
    if ((request.layout ?? "Preserve") === "Preserve" && preserveLayoutError && !targetTopology.isFetching) {
      setRequest((current) => ({ ...current, layout: "Redistribute" }));
    }
  }, [preserveLayoutError, request.layout, targetTopology.isFetching]);

  useEffect(() => {
    if (!isRedistribute) {
      setSelectedTargetShards([]);
      return;
    }

    const allTargetShards = targetShardOptions.map((shard) => shard.value);
    if (allTargetShards.length === 0) {
      setSelectedTargetShards([]);
      return;
    }

    setSelectedTargetShards((current) => {
      const available = new Set(allTargetShards);
      const stillValid = current.filter((value) => available.has(value)).sort((a, b) => a - b);
      return stillValid.length > 0 ? stillValid : allTargetShards;
    });
  }, [isRedistribute, request.targetClusterId, targetShardOptions]);


  const entityPlanRequest = (): EntityRestorePlanRequest => ({
    policyId: selectedBackup?.policyId ?? null,
    anchorBackupId: request.backupId,
    targetClusterId: request.targetClusterId,
    database: "",
    table: "",
    targetDatabase: null,
    targetTable: null,
    append: request.append,
    allowSchemaMismatch: request.allowSchemaMismatch,
    layout: request.layout ?? "Preserve",
    sourceShard: null,
    targetShard: null,
    tables: selectedMappings.map(({ selected: _, ...mapping }) => ({
      ...mapping,
      targetDatabase: mapping.targetDatabase || null,
      targetTable: mapping.targetTable || null,
      append: mapping.schemaOnly ? false : mapping.append ?? false,
      allowSchemaMismatch: mapping.allowSchemaMismatch ?? false,
      schemaOnly: mapping.schemaOnly ?? false,
      shardSources: mapping.shardSources ?? []
    })),
    schemaOnly: request.schemaOnly,
    sourceShards: selectedSourceShards.length === 0 || selectedSourceShards.length === sourceShardOptions.length ? null : selectedSourceShards,
    targetShards: isRedistribute && targetShardOptions.length > 0 && selectedTargetShards.length > 0 && selectedTargetShards.length < targetShardOptions.length ? selectedTargetShards : null,
    confirmDestructive: false,
    clickHouseRestoreSettings: clickHouseSettings
  });

  const restorePlan = useQuery({
    queryKey: ["restore-plan", request.backupId, request.targetClusterId, request.layout, selectedSourceShards, selectedTargetShards, selectedMappings, clickHouseSettings],
    queryFn: () => api.restorePlan(entityPlanRequest()),
    enabled: Boolean(selectedBackup?.policyId && request.backupId && request.targetClusterId && selectedMappings.length > 0 && settingsValid),
    retry: false
  });
  const restorePlanBlocked = step === 3 && Boolean(selectedBackup?.policyId) && (restorePlan.isLoading || restorePlan.isError);

  const restoreRequest = (): InitiateRestoreRequest => ({
    ...request,
    sourceShard: null,
    targetShard: null,
    sourceShards: selectedSourceShards.length === 0 || selectedSourceShards.length === sourceShardOptions.length ? null : selectedSourceShards,
    targetShards: isRedistribute && targetShardOptions.length > 0 && selectedTargetShards.length > 0 && selectedTargetShards.length < targetShardOptions.length ? selectedTargetShards : null,
    clickHouseRestoreSettings: clickHouseSettings,
    tables: selectedMappings.map(({ selected: _, ...mapping }) => ({
      ...mapping,
      targetDatabase: mapping.targetDatabase || null,
      targetTable: mapping.targetTable || null,
      append: mapping.schemaOnly ? false : mapping.append ?? false,
      allowSchemaMismatch: mapping.allowSchemaMismatch ?? false,
      schemaOnly: mapping.schemaOnly ?? false
    }))
  });

  const mutation = useMutation({
    mutationFn: () => selectedBackup?.policyId ? api.initiateRestoreFromPlan({ ...entityPlanRequest(), confirmDestructive: true }) : api.initiateRestore({ ...restoreRequest(), confirmDestructive: true }),
    onSuccess: (restore) => {
      showToast({ kind: "success", text: "Restore queued. Opening details." });
      queryClient.setQueryData(["restore", restore.id], restore);
      queryClient.invalidateQueries({ queryKey: ["restores"] });
      navigate(`/restores/${restore.id}`);
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  const canQueueRestore = restoreErrors.length === 0 && settingsValid && !settingsPreviewBlocked && !restorePlanBlocked && !mutation.isPending;
  const queueRestore = () => setShowConfirm(true);
  const confirmRestore = () => {
    setShowConfirm(false);
    mutation.mutate();
  };

  return (
    <Page title="Start restore" subtitle="Choose a backup, destination cluster, tables, and shard layout for a restore." action={<Link className="secondary" to="/restores"><ArrowLeft size={16} /> Restore history</Link>}>
      <section className="restore-workbench">
        <div className="restore-main panel">
          <RestoreStepper step={step} errors={restoreErrors} onStep={setStep} />
          <div className="restore-step-body">
            {step === 0 && <BackupChoiceStep isLoading={backups.isLoading} backups={restorableBackups} selectedBackupId={request.backupId} onSelect={(backupId) => setRequest({ ...request, backupId })} onOpenBackup={setSelectedBackupDetailId} clusterName={(clusterId) => clusterById.get(clusterId)?.name ?? clusterId} policyName={(policyId) => policyId ? policyById.get(policyId)?.name ?? policyId : "Manual"} dateFilterValue={backupDateFilter} activeWindowHours={activeBackupWindowHours} onDateFilterChange={(value) => { setBackupDateFilter(value); setActiveBackupWindowHours(null); }} onPreset={(hours) => { setBackupDateFilter(backupDateFromHours(hours)); setActiveBackupWindowHours(hours); }} />}
            {step === 1 && <DestinationStep request={request} onChange={setRequest} clusters={clusters.data ?? []} targetShardOptions={targetShardOptions} selectedTargetShards={selectedTargetShards} onTargetShardsChange={setSelectedTargetShards} targetShardsLoading={targetTopology.isFetching} preserveLayoutDisabled={preserveLayoutDisabled} preserveLayoutReason={preserveLayoutReason} />}
            {step === 2 && <ScopeStep backup={selectedBackup} mappings={mappings} onMappingsChange={setMappings} sourceShardOptions={sourceShardOptions} selectedSourceShards={selectedSourceShards} onSourceShardsChange={setSelectedSourceShards} schemaByTableId={schemaByTableId} schemaLoading={backupSchema.isFetching} plan={restorePlan.data ?? null} planLoading={restorePlan.isLoading || restorePlan.isFetching} planError={restorePlan.error ? String(restorePlan.error) : null} restoreToDate={restoreToDate} onRestoreToDateChange={setRestoreToDate} />}
            {step === 3 && <>
              <ReviewStep backup={selectedBackup} targetClusterName={clusterById.get(request.targetClusterId)?.name ?? request.targetClusterId} request={request} mappings={selectedMappings} sourceShardOptions={sourceShardOptions} selectedSourceShards={selectedSourceShards} targetShardOptions={targetShardOptions} selectedTargetShards={selectedTargetShards} errors={restoreErrors} plan={restorePlan.data ?? null} planError={restorePlan.error ? String(restorePlan.error) : null} />
              <ClickHouseAdvancedSettingsEditor title="ClickHouse restore settings for this run" value={clickHouseSettings} sources={(settingsPreview.data?.sources ?? []) as any} onChange={setClickHouseSettings} onValidityChange={setSettingsValid} />
              {settingsPreview.isError && <span className="field-error">{String(settingsPreview.error)}</span>}
            </>}
          </div>
          <div className="restore-wizard-actions">
            <button className="ghost" disabled={step === 0} onClick={() => setStep((current) => Math.max(0, current - 1) as RestoreStep)}><ArrowLeft size={16} /> Back</button>
            {step < 3
              ? <button className="primary" disabled={stepErrors.length > 0} onClick={() => setStep((current) => Math.min(3, current + 1) as RestoreStep)}>Continue <ArrowRight size={16} /></button>
              : <button className="primary" disabled={!canQueueRestore} onClick={queueRestore}><RotateCcw size={16} /> Queue restore</button>}
          </div>
        </div>
        <ImpactSummary backup={selectedBackup} targetClusterName={clusterById.get(request.targetClusterId)?.name ?? "Not selected"} request={request} mappings={selectedMappings} sourceShardOptions={sourceShardOptions} selectedSourceShards={selectedSourceShards} targetShardOptions={targetShardOptions} selectedTargetShards={selectedTargetShards} errors={restoreErrors} />
      </section>
      {showConfirm && <ConfirmDialog title="Confirm destructive restore" message="Queue this restore? It may append data, allow schema mismatch, or write into an existing target table." confirmLabel="Confirm restore" busy={mutation.isPending} onConfirm={confirmRestore} onCancel={() => setShowConfirm(false)} />}
      {selectedBackupDetailId && <BackupDrawer backupId={selectedBackupDetailId} onClose={() => setSelectedBackupDetailId(null)} onOpenBackup={setSelectedBackupDetailId} />}
    </Page>
  );
}

function backupDateFromHours(hours: number) {
  const date = new Date(Date.now() - hours * 60 * 60 * 1000);
  return date.toISOString().slice(0, 10);
}

function backupMatchesDateFilter(createdAt: string, dateValue: string) {
  if (!dateValue) return true;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(dateValue)) return true;
  const cutoff = new Date(`${dateValue}T00:00:00`);
  return new Date(createdAt).getTime() >= cutoff.getTime();
}
