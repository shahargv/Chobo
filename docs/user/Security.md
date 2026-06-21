# Security

Chobo stores access to important systems: ClickHouse, S3-compatible backup storage, and Chobo's own management API. Treat it as production infrastructure.

## Network Exposure

Do not expose ChoboServer management directly to the public internet.

Recommended production shape:

- place ChoboServer on a private network;
- expose it only to DBA workstations, VPN, bastion, or internal admin networks;
- terminate TLS at a trusted reverse proxy, ingress, or load balancer;
- restrict inbound access with firewall or security-group rules.

The web GUI and API are served from the same listener by default. Anyone with a valid token can operate Chobo through either surface.

## Encryption Key

`Chobo:EncryptionKeyBase64` protects stored ClickHouse and S3 credentials.

Generate a stable 32-byte Base64 key:

```bash
openssl rand -base64 32
```

PowerShell:

```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Use a secret manager or orchestrator secret, not a hard-coded value in source control.

Do not change this key casually. If you change it without re-entering credentials, existing encrypted ClickHouse and S3 credentials become unreadable.

## Persistent Data

Persist `Chobo:DataDirectory`. It contains:

- `chobo.db`, the SQLite metadata database;
- initialization state;
- local application log storage;
- SQLite self-backups if enabled and stored under the data directory.

Protect this directory with filesystem permissions and normal server backups.

## Access Tokens

Raw access tokens are shown only when created. Store them in a password manager or secret manager.

Recommended practice:

- use separate users or tokens for humans and automation;
- rotate tokens after staff changes or automation host rebuilds;
- remove unused tokens;
- do not paste tokens into issue trackers, screenshots, or shared logs.

CLI token setup:

```powershell
ChoboCli server auth --server-url https://chobo.example.com --access-token <token>
```

## ClickHouse Permissions

Use a dedicated ClickHouse user for Chobo. It needs enough permission to:

- read ClickHouse topology and table metadata;
- run `BACKUP TABLE ... TO S3(...) ASYNC`;
- run `RESTORE TABLE ... FROM S3(...) ASYNC` when restoring;
- create target databases and tables during restores;
- query `system.backups` while operations run.

Limit the account to the databases Chobo should protect or restore when your ClickHouse permission model allows it.

## S3 Credentials

Use a dedicated S3 identity for Chobo backup targets.

It should have permissions for the configured bucket and path prefix:

- list objects;
- put backup objects and metadata manifests;
- read objects for restore;
- delete objects for retention and manual deletion.

If you use a path prefix, scope the S3 policy to that prefix where possible.

## Imports And Secrets

Data export/import excludes audit entries and application logs. Export envelopes may carry encrypted credential fields for compatibility, but import treats ClickHouse and S3 credentials as empty.

After import:

```powershell
ChoboCli clusters update-credentials --id <cluster-id> --username <user> --password <password>
ChoboCli targets update-s3 --id <target-id> --name prod-s3 --endpoint https://s3.example.com --bucket chobo-backups --access-key <key> --secret-key <secret>
```

Never import raw access tokens, decrypted credentials, or local AES key material from another server.


