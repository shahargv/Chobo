import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import type { BackupTargetDto, JsonValue, UpsertS3TargetRequest } from "../api/generated";
import { useApi } from "../api-context";
import { CrudPage, DataTable, Input, Select } from "../components/ui";

const storageTypes = [["s3", "S3-compatible object storage"]];

export function Targets() {
  const { api, showToast } = useApi();
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const [showForm, setShowForm] = useState(false);
  const [modifyCredentials, setModifyCredentials] = useState(true);
  const [editing, setEditing] = useState<BackupTargetDto | null>(null);
  const [storageType, setStorageType] = useState("s3");
  const [draft, setDraft] = useState<UpsertS3TargetRequest>(emptyDraft());

  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setModifyCredentials(true);
    setStorageType("s3");
    setDraft(emptyDraft());
  };

  const save = useMutation({
    mutationFn: () => editing
      ? api.updateTarget(editing.id, modifyCredentials ? draft : { ...draft, accessKey: null, secretKey: null })
      : api.addTarget(draft),
    onSuccess: () => { showToast({ kind: "success", text: "Backup storage saved." }); targets.refetch(); reset(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });

  return <CrudPage title="Backup Storage" subtitle="Manage backup storage targets and connection settings." showForm={showForm} onAdd={() => { reset(); setShowForm(true); }} formTitle={editing ? "Edit backup storage" : "Create backup storage"} saveLabel={editing ? "Update storage" : "Save storage"} onCancel={reset} onSave={() => save.mutate()} form={<>
    <Select label="Storage type" value={storageType} onChange={setStorageType} options={storageTypes} />
    {storageType === "s3" && <>
      <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
      <Input label="Endpoint" value={draft.endpoint} onChange={(value) => setDraft({ ...draft, endpoint: value })} />
      <Input label="Bucket" value={draft.bucket} onChange={(value) => setDraft({ ...draft, bucket: value })} />
      <Input label="Region" value={draft.region} onChange={(value) => setDraft({ ...draft, region: value })} />
      <Input label="Path prefix" value={draft.pathPrefix ?? ""} onChange={(value) => setDraft({ ...draft, pathPrefix: value || null })} />
      <div className="field-wide s3-addressing">
        <span className="field-label">S3 addressing style</span>
        <div className="segmented">
          <button type="button" className={draft.forcePathStyle ? "selected" : "ghost"} onClick={() => setDraft({ ...draft, forcePathStyle: true })}>Path style</button>
          <button type="button" className={!draft.forcePathStyle ? "selected" : "ghost"} onClick={() => setDraft({ ...draft, forcePathStyle: false })}>Virtual-host style</button>
        </div>
      </div>
      {editing && <label className="checkbox-row"><input type="checkbox" checked={modifyCredentials} onChange={(event) => setModifyCredentials(event.target.checked)} /> Modify credentials</label>}
      {(!editing || modifyCredentials) && <Input label="Access key" value={draft.accessKey ?? ""} onChange={(value) => setDraft({ ...draft, accessKey: value || null })} />}
      {(!editing || modifyCredentials) && <Input label="Secret key" type="password" value={draft.secretKey ?? ""} onChange={(value) => setDraft({ ...draft, secretKey: value || null })} />}
    </>}
  </>} table={<DataTable headers={["Name", "Type", "Endpoint", "Bucket", "Addressing", "Prefix", "Actions"]} isLoading={targets.isLoading}>{(targets.data ?? []).map((target) => {
    const settings = s3Settings(target);
    return <tr key={target.id}><td>{target.name}</td><td>{target.type}</td><td>{settings.endpoint}</td><td>{settings.bucket}</td><td>{settings.forcePathStyle ? "Path style" : "Virtual-host style"}</td><td>{settings.pathPrefix ?? ""}</td><td className="actions"><button className="ghost" onClick={() => {
      setEditing(target);
      setShowForm(true);
      setStorageType(target.type);
      setModifyCredentials(false);
      setDraft({ name: target.name, endpoint: settings.endpoint, region: settings.region, bucket: settings.bucket, pathPrefix: settings.pathPrefix, forcePathStyle: settings.forcePathStyle, accessKey: null, secretKey: null });
    }}>Edit</button><button className="ghost" onClick={() => api.testTarget(target.id).then((x) => showToast({ kind: x.succeeded ? "success" : "error", text: x.message }))}>Test</button></td></tr>;
  })}</DataTable>} />;
}

function emptyDraft(): UpsertS3TargetRequest {
  return { name: "", endpoint: "http://localhost:9000", region: "us-east-1", bucket: "", pathPrefix: null, forcePathStyle: true, accessKey: null, secretKey: null };
}

function s3Settings(target: BackupTargetDto) {
  return {
    endpoint: stringSetting(target.settings.endpoint),
    region: stringSetting(target.settings.region, "us-east-1"),
    bucket: stringSetting(target.settings.bucket),
    pathPrefix: nullableStringSetting(target.settings.pathPrefix),
    forcePathStyle: booleanSetting(target.settings.forcePathStyle, true)
  };
}

function stringSetting(value: JsonValue | undefined, fallback = "") {
  return typeof value === "string" ? value : fallback;
}

function nullableStringSetting(value: JsonValue | undefined) {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function booleanSetting(value: JsonValue | undefined, fallback: boolean) {
  return typeof value === "boolean" ? value : fallback;
}