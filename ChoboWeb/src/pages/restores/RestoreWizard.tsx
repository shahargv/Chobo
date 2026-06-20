import { useEffect, useMemo, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, RotateCcw } from "lucide-react";
import type { InitiateRestoreRequest } from "../../api/generated";
import { useApi } from "../../api-context";
import { Page } from "../../components/ui";
import type { RestoreMappingDraft, RestoreStep } from "./restoreTypes";
import { BackupChoiceStep, DestinationStep, ImpactSummary, RestoreStepper, ReviewStep, ScopeStep } from "./RestoreWizardSteps";
import { getMissingPreserveTargetShards, getRequestedBackupId, getSourceShardOptions, getTargetShardOptions, isBackupRestorable, restoreTargetTableName, validateRestoreRequest, validateStep } from "./restoreUtils";

export function RestoreWizard() {
  const { api, showToast } = useApi();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const backups = useQuery({ queryKey: ["backups"], queryFn: () => api.backups() });
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const [step, setStep] = useState<RestoreStep>(0);
  const [request, setRequest] = useState<InitiateRestoreRequest>({ backupId: "", targetClusterId: "", append: false, allowSchemaMismatch: false, layout: "Preserve", schemaOnly: false });
  const [mappings, setMappings] = useState<RestoreMappingDraft[]>([]);
  const [selectedSourceShards, setSelectedSourceShards] = useState<number[]>([]);
  const [selectedTargetShards, setSelectedTargetShards] = useState<number[]>([]);
  const requestedBackupId = getRequestedBackupId(location.state);
  const isRedistribute = (request.layout ?? "Preserve") === "Redistribute";
  const targetTopology = useQuery({
    queryKey: ["cluster-topology", request.targetClusterId],
    queryFn: () => api.clusterTopology(request.targetClusterId),
    enabled: Boolean(request.targetClusterId)
  });
  const clusterById = useMemo(() => new Map((clusters.data ?? []).map((cluster) => [cluster.id, cluster])), [clusters.data]);
  const restorableBackups = useMemo(() => (backups.data ?? []).filter(isBackupRestorable), [backups.data]);
  const selectedBackup = restorableBackups.find((backup) => backup.id === request.backupId) ?? null;
  const sourceShardOptions = useMemo(() => getSourceShardOptions(selectedBackup), [selectedBackup]);
  const targetShardOptions = useMemo(() => getTargetShardOptions(targetTopology.data?.shards), [targetTopology.data]);
  const isDifferentCluster = Boolean(selectedBackup && request.targetClusterId && selectedBackup.sourceClusterId !== request.targetClusterId);
  const preserveMissingTargetShards = isDifferentCluster && targetTopology.isSuccess ? getMissingPreserveTargetShards(selectedSourceShards, targetShardOptions) : [];
  const preserveLayoutError = isDifferentCluster && targetTopology.isFetching
    ? "Preserve layout is checking target topology."
    : preserveMissingTargetShards.length > 0 ? `Preserve layout needs target shard${preserveMissingTargetShards.length === 1 ? "" : "s"} ${preserveMissingTargetShards.join(", ")}. Choose redistribute for this target cluster.` : null;
  const preserveLayoutDisabled = Boolean(preserveLayoutError);
  const preserveLayoutReason = preserveLayoutError;
  const selectedMappings = mappings.filter((mapping) => mapping.selected);
  const restoreErrors = validateRestoreRequest(request, mappings, selectedSourceShards, sourceShardOptions.length, selectedTargetShards, targetShardOptions.length, preserveLayoutError);
  const stepErrors = validateStep(step, request, mappings, selectedSourceShards, sourceShardOptions.length, selectedTargetShards, targetShardOptions.length, preserveLayoutError);

  useEffect(() => {
    if (!requestedBackupId || request.backupId || !restorableBackups.some((backup) => backup.id === requestedBackupId)) return;
    setRequest((current) => ({ ...current, backupId: requestedBackupId }));
    setStep(1);
  }, [request.backupId, requestedBackupId, restorableBackups]);

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
      selected: false
    })));
    setSelectedSourceShards(getSourceShardOptions(selectedBackup).map((shard) => shard.value));
  }, [selectedBackup?.id]);

  useEffect(() => {
    if ((request.layout ?? "Preserve") === "Preserve" && preserveMissingTargetShards.length > 0) {
      setRequest((current) => ({ ...current, layout: "Redistribute" }));
    }
  }, [preserveMissingTargetShards, request.layout]);

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

  const restoreRequest = (): InitiateRestoreRequest => ({
    ...request,
    sourceShard: null,
    targetShard: null,
    sourceShards: selectedSourceShards.length === 0 || selectedSourceShards.length === sourceShardOptions.length ? null : selectedSourceShards,
    targetShards: isRedistribute && targetShardOptions.length > 0 && selectedTargetShards.length > 0 && selectedTargetShards.length < targetShardOptions.length ? selectedTargetShards : null,
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
    mutationFn: () => api.initiateRestore(restoreRequest()),
    onSuccess: (restore) => {
      showToast({ kind: "success", text: "Restore queued. Opening details." });
      queryClient.setQueryData(["restore", restore.id], restore);
      queryClient.invalidateQueries({ queryKey: ["restores"] });
      navigate(`/restores/${restore.id}`);
    },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  return (
    <Page title="Start restore" action={<Link className="secondary" to="/restores"><ArrowLeft size={16} /> Restore history</Link>}>
      <section className="restore-workbench">
        <div className="restore-main panel">
          <RestoreStepper step={step} errors={restoreErrors} onStep={setStep} />
          <div className="restore-step-body">
            {step === 0 && <BackupChoiceStep backups={restorableBackups} selectedBackupId={request.backupId} onSelect={(backupId) => setRequest({ ...request, backupId })} clusterName={(clusterId) => clusterById.get(clusterId)?.name ?? clusterId} />}
            {step === 1 && <DestinationStep request={request} onChange={setRequest} clusters={clusters.data ?? []} targetShardOptions={targetShardOptions} selectedTargetShards={selectedTargetShards} onTargetShardsChange={setSelectedTargetShards} targetShardsLoading={targetTopology.isFetching} preserveLayoutDisabled={preserveLayoutDisabled} preserveLayoutReason={preserveLayoutReason} />}
            {step === 2 && <ScopeStep backup={selectedBackup} mappings={mappings} onMappingsChange={setMappings} sourceShardOptions={sourceShardOptions} selectedSourceShards={selectedSourceShards} onSourceShardsChange={setSelectedSourceShards} />}
            {step === 3 && <ReviewStep backup={selectedBackup} targetClusterName={clusterById.get(request.targetClusterId)?.name ?? request.targetClusterId} request={request} mappings={selectedMappings} sourceShardOptions={sourceShardOptions} selectedSourceShards={selectedSourceShards} targetShardOptions={targetShardOptions} selectedTargetShards={selectedTargetShards} errors={restoreErrors} />}
          </div>
          <div className="restore-wizard-actions">
            <button className="ghost" disabled={step === 0} onClick={() => setStep((current) => Math.max(0, current - 1) as RestoreStep)}><ArrowLeft size={16} /> Back</button>
            {step < 3
              ? <button className="primary" disabled={stepErrors.length > 0} onClick={() => setStep((current) => Math.min(3, current + 1) as RestoreStep)}>Continue <ArrowRight size={16} /></button>
              : <button className="primary" disabled={restoreErrors.length > 0 || mutation.isPending} onClick={() => mutation.mutate()}><RotateCcw size={16} /> Queue restore</button>}
          </div>
        </div>
        <ImpactSummary backup={selectedBackup} targetClusterName={clusterById.get(request.targetClusterId)?.name ?? "Not selected"} request={request} mappings={selectedMappings} sourceShardOptions={sourceShardOptions} selectedSourceShards={selectedSourceShards} targetShardOptions={targetShardOptions} selectedTargetShards={selectedTargetShards} errors={restoreErrors} />
      </section>
    </Page>
  );
}


