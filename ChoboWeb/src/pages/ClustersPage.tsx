import { useEffect, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useParams } from "react-router-dom";
import type { ClusterDto, UpsertClusterRequest } from "../api/generated";
import { useApi } from "../api-context";
import { CrudPage, DataTable, Input, Select } from "../components/ui";
export function Clusters() {
  const { api, showToast } = useApi();
  const { clusterId } = useParams();
  const clusters = useQuery({ queryKey: ["clusters"], queryFn: () => api.clusters() });
  const [showForm, setShowForm] = useState(false);
  const [modifyCredentials, setModifyCredentials] = useState(true);
  const [editing, setEditing] = useState<ClusterDto | null>(null);
  const [draft, setDraft] = useState<UpsertClusterRequest>({ name: "", mode: "SingleInstance", accessNodes: [{ host: "localhost", port: 9000, useTls: false }], userName: null, password: null });
  const clickHouseNames = useQuery({ queryKey: ["clickhouse-cluster-names", editing?.id], queryFn: () => api.clickHouseClusterNames(editing!.id), enabled: !!editing && draft.mode === "Cluster", retry: false });
  const editCluster = (cluster: ClusterDto) => {
    setEditing(cluster);
    setShowForm(true);
    setModifyCredentials(false);
    setDraft({ name: cluster.name, mode: cluster.mode, accessNodes: cluster.accessNodes.map((node) => ({ host: node.host, port: node.port, useTls: node.useTls })), userName: null, password: null, backupRestoreMaxDop: cluster.backupRestoreMaxDop, clickHouseClusterName: cluster.clickHouseClusterName });
  };
  useEffect(() => {
    if (!clusterId || !clusters.data) return;
    const cluster = clusters.data.find((item) => item.id === clusterId);
    if (cluster) editCluster(cluster);
  }, [clusterId, clusters.data]);
  const reset = () => {
    setShowForm(false);
    setEditing(null);
    setModifyCredentials(true);
    setDraft({ name: "", mode: "SingleInstance", accessNodes: [{ host: "localhost", port: 9000, useTls: false }], userName: null, password: null });
  };
  const save = useMutation({
    mutationFn: () => editing ? api.updateCluster(editing.id, modifyCredentials ? draft : { ...draft, userName: null, password: null }) : api.addCluster(draft),
    onSuccess: () => { showToast({ kind: "success", text: "Cluster saved." }); clusters.refetch(); reset(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <CrudPage title="ClickHouse Clusters" subtitle="Register ClickHouse clusters, access nodes, credentials, and backup topology settings." showForm={showForm} onAdd={() => { reset(); setShowForm(true); }} formTitle={editing ? "Edit ClickHouse cluster" : "Create ClickHouse cluster"} saveLabel={editing ? "Update cluster" : "Save cluster"} onCancel={reset} onSave={() => save.mutate()} form={<>
      <Input label="Name" value={draft.name} onChange={(value) => setDraft({ ...draft, name: value })} />
      <Select label="Mode" value={draft.mode} onChange={(value) => setDraft({ ...draft, mode: value as UpsertClusterRequest["mode"] })} options={[["SingleInstance", "Single instance"], ["Cluster", "Cluster"]]} />
      <Input label="Access nodes" value={formatNodes(draft.accessNodes)} onChange={(value) => setDraft({ ...draft, accessNodes: parseNodes(value, draft.accessNodes.some((node) => node.useTls)) })} />
      <Input label="Max DOP" type="number" value={draft.backupRestoreMaxDop?.toString() ?? ""} onChange={(value) => setDraft({ ...draft, backupRestoreMaxDop: value ? Number(value) : null })} />
      {draft.mode === "Cluster" && (editing
        ? <Select label="ClickHouse system.clusters name" value={draft.clickHouseClusterName ?? ""} onChange={(value) => setDraft({ ...draft, clickHouseClusterName: value || null })} options={clickHouseClusterNameOptions(clickHouseNames.data?.names ?? [], draft.clickHouseClusterName)} />
        : <Input label="ClickHouse system.clusters name" value={draft.clickHouseClusterName ?? ""} onChange={(value) => setDraft({ ...draft, clickHouseClusterName: value || null })} />)}
      {draft.mode === "Cluster" && <span className="hint field-wide">{editing ? "Enter one or more reachable ClickHouse representatives. Chobo reads system.clusters through them and discovers the shard/replica nodes automatically. Choose the ClickHouse topology entry Chobo should use, or leave it empty when ClickHouse exposes exactly one cluster." : "Enter one or more reachable ClickHouse representatives. Chobo reads system.clusters through them and discovers the shard/replica nodes automatically. After saving the connection, edit this cluster to choose from the live system.clusters list."}</span>}
      <label className="checkbox-row"><input type="checkbox" checked={draft.accessNodes.some((node) => node.useTls)} onChange={(event) => setDraft({ ...draft, accessNodes: draft.accessNodes.map((node) => ({ ...node, useTls: event.target.checked })) })} /> Use TLS</label>
      {editing && <label className="checkbox-row"><input type="checkbox" checked={modifyCredentials} onChange={(event) => setModifyCredentials(event.target.checked)} /> Modify credentials</label>}
      {(!editing || modifyCredentials) && <Input label="Username" value={draft.userName ?? ""} onChange={(value) => setDraft({ ...draft, userName: value || null })} />}
      {(!editing || modifyCredentials) && <Input label="Password" type="password" value={draft.password ?? ""} onChange={(value) => setDraft({ ...draft, password: value || null })} />}
    </>} table={<DataTable headers={["Name", "Mode", "Access nodes", "Max DOP", "Actions"]} isLoading={clusters.isLoading}>{(clusters.data ?? []).map((cluster) => <tr key={cluster.id}><td>{cluster.name}</td><td>{cluster.mode}</td><td>{cluster.accessNodes.map((node) => `${node.host}:${node.port}`).join(", ")}</td><td>{cluster.backupRestoreMaxDop ?? "default"}</td><td className="actions"><button className="ghost" onClick={() => editCluster(cluster)}>Edit</button><button className="ghost" onClick={() => api.testCluster(cluster.id).then((x) => showToast({ kind: x.succeeded ? "success" : "error", text: x.message }))}>Test</button></td></tr>)}</DataTable>} />
  );
}

function formatNodes(nodes: UpsertClusterRequest["accessNodes"]) {
  return nodes.map((node) => `${node.host}:${node.port}`).join(", ");
}

function parseNodes(value: string, useTls: boolean): UpsertClusterRequest["accessNodes"] {
  const nodes = value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => {
      const [host, port] = item.split(":");
      return { host, port: Number(port) || 9000, useTls };
    });

  return nodes.length > 0 ? nodes : [{ host: "", port: 9000, useTls }];
}

function clickHouseClusterNameOptions(names: string[], current?: string | null) {
  const options = [...names];
  if (current && !options.includes(current)) options.unshift(current);
  return options.map((name) => [name, name]);
}


