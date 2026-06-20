import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import type { BackupTargetDto, UpsertS3TargetRequest } from "../api/generated";
import { useApi } from "../api-context";
import { CrudPage, DataTable, Input } from "../components/ui";
export function Targets() {
  const { api, showToast } = useApi();
  const targets = useQuery({ queryKey: ["targets"], queryFn: () => api.targets() });
  const [showForm, setShowForm] = useState(false);
  const [modifyCredentials, setModifyCredentials] = useState(true);
  const [editing, setEditing] = useState<BackupTargetDto | null>(null);
  const [draft, setDraft] = useState<UpsertS3TargetRequest>({ name: "", endpoint: "http://localhost:9000", region: "us-east-1", bucket: "", pathPrefix: null, forcePathStyle: true, accessKey: null, secretKey: null });
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setModifyCredentials(true);
    setDraft({ name: "", endpoint: "http://localhost:9000", region: "us-east-1", bucket: "", pathPrefix: null, forcePathStyle: true, accessKey: null, secretKey: null });
  };
  const save = useMutation({
    mutationFn: () => editing ? api.updateTarget(editing.id, modifyCredentials ? draft : { ...draft, accessKey: null, secretKey: null }) : api.addTarget(draft),
    onSuccess: () => { showToast({ kind: "success", text: "Backup storage saved." }); targets.refetch(); reset(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return <CrudPage title="Backup Storage" showForm={showForm} onAdd={() => { reset(); setShowForm(true); }} formTitle={editing ? "Edit backup storage" : "Create backup storage"} saveLabel={editing ? "Update storage" : "Save storage"} onCancel={reset} onSave={() => save.mutate()} form={<>
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
      <span className="hint">
        {draft.forcePathStyle
          ? "Uses endpoint/bucket/key. Best for MinIO and many S3-compatible services."
          : "Uses bucket.endpoint/key. Use when your S3 provider requires virtual-hosted buckets."}
      </span>
    </div>
    {editing && <label className="checkbox-row"><input type="checkbox" checked={modifyCredentials} onChange={(event) => setModifyCredentials(event.target.checked)} /> Modify credentials</label>}
    {(!editing || modifyCredentials) && <Input label="Access key" value={draft.accessKey ?? ""} onChange={(value) => setDraft({ ...draft, accessKey: value || null })} />}
    {(!editing || modifyCredentials) && <Input label="Secret key" type="password" value={draft.secretKey ?? ""} onChange={(value) => setDraft({ ...draft, secretKey: value || null })} />}
  </>} table={<DataTable headers={["Name", "Endpoint", "Bucket", "Addressing", "Prefix", "Actions"]}>{(targets.data ?? []).map((target) => <tr key={target.id}><td>{target.name}</td><td>{target.s3.endpoint}</td><td>{target.s3.bucket}</td><td>{target.s3.forcePathStyle ? "Path style" : "Virtual-host style"}</td><td>{target.s3.pathPrefix ?? ""}</td><td className="actions"><button className="ghost" onClick={() => {
    setEditing(target);
    setShowForm(true);
    setModifyCredentials(false);
    setDraft({ name: target.name, endpoint: target.s3.endpoint, region: target.s3.region, bucket: target.s3.bucket, pathPrefix: target.s3.pathPrefix, forcePathStyle: target.s3.forcePathStyle, accessKey: null, secretKey: null });
  }}>Edit</button><button className="ghost" onClick={() => api.testTarget(target.id).then((x) => showToast({ kind: x.succeeded ? "success" : "error", text: x.message }))}>Test</button></td></tr>)}</DataTable>} />;
}

