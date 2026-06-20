import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Users } from "lucide-react";
import type { AccessTokenDto, CreateAccessTokenResponse, CreateUserResponse, UserDto } from "../api/generated";
import { useApi } from "../api-context";
import { DataTable, Input, Page } from "../components/ui";
import { formatTime } from "../utils/format";
export function UsersPage() {
  const { api, showToast } = useApi();
  const users = useQuery({ queryKey: ["users"], queryFn: () => api.users() });
  const [showForm, setShowForm] = useState(false);
  const [name, setName] = useState("");
  const [oneTimeToken, setOneTimeToken] = useState<CreateUserResponse | CreateAccessTokenResponse | null>(null);
  const create = useMutation({
    mutationFn: () => api.addUser({ userName: name }),
    onSuccess: (result) => { setOneTimeToken(result); setShowForm(false); setName(""); showToast({ kind: "success", text: "User created. Copy the token now." }); users.refetch(); },
    onError: (error) => showToast({ kind: "error", text: String(error) })
  });
  return (
    <Page title="Users" subtitle="Create users, manage API tokens, and deactivate access when it is no longer needed." action={<button className="primary" onClick={() => setShowForm(true)}><Users size={16} /> Add user</button>}>
      <section className="panel">
        <DataTable headers={["User", "Active", "Created", "Actions"]}>{(users.data ?? []).map((user) => <tr key={user.id}><td>{user.userName}</td><td>{user.isActive ? "yes" : "no"}</td><td>{formatTime(user.createdAt)}</td><td className="actions"><UserActions user={user} /></td></tr>)}</DataTable>
      </section>
      {showForm && (
      <section className="panel form-panel">
        <div className="section-head"><h2>Create user</h2><button className="ghost" onClick={() => setShowForm(false)}>Cancel</button></div>
        <Input label="User name" value={name} onChange={setName} />
        <button className="primary" onClick={() => create.mutate()}><Users size={16} /> Add user</button>
      </section>)}
      {oneTimeToken && <section className="panel"><h2>One-time token</h2><pre className="token-box">{oneTimeToken.accessToken}</pre></section>}
    </Page>
  );
}

function UserActions({ user }: { user: UserDto }) {
  const { api, showToast } = useApi();
  const tokens = useQuery({ queryKey: ["tokens", user.id], queryFn: () => api.tokens(user.id), enabled: false });
  return <><button className="ghost" onClick={() => tokens.refetch()}>Tokens</button>{tokens.data?.map((token: AccessTokenDto) => <span className="chip" key={token.id}>{token.name}</span>)}<button className="ghost" onClick={() => api.addToken(user.id, { name: "browser" }).then((result) => showToast({ kind: "success", text: `Token: ${result.accessToken}` }))}>New token</button></>;
}

