import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const snapshotPath = resolve(root, "openapi/chobo.v1.json");
const generatedPath = resolve(root, "src/api/generated.ts");
const snapshot = JSON.parse(readFileSync(snapshotPath, "utf8"));
const schemas = snapshot.components?.schemas ?? {};
const schemaNames = Object.keys(schemas).sort();

const nullableProperties = new Set([
  "accessKey",
  "actorUserId",
  "allowSchemaMismatch",
  "append",
  "backupRestoreMaxDop",
  "backupPath",
  "clickHouseClusterName",
  "clickHouseOperationId",
  "clickHouseStatus",
  "clickHouseBackupSettings",
  "clickHouseRestoreSettings",
  "currentRunReason",
  "deactivatedAt",
  "deletedAt",
  "deletionError",
  "deletionReason",
  "deletionRequestedAt",
  "deletionStartedAt",
  "description",
  "details",
  "encryptedAccessKey",
  "encryptedAccessKeyKeyId",
  "encryptedPassword",
  "encryptedPasswordKeyId",
  "encryptedSecretKey",
  "encryptedSecretKeyKeyId",
  "encryptedUserName",
  "encryptedUserNameKeyId",
  "endedAt",
  "error",
  "exception",
  "failureReason",
  "fullRetentionMinutes",
  "incrementalRetentionMinutes",
  "lastCompletedAt",
  "lastError",
  "lastRunAt",
  "lastRunFailureReason",
  "lastRunStatus",
  "lastStartedAt",
  "lastSuccessfulRunCompletedAt",
  "layout",
  "manualRequestJson",
  "missedRunGracePeriod",
  "parentFullBackupId",
  "parentFullBackupTableId",
  "parentFullBackupTableShardId",
  "pathPrefix",
  "pinnedAt",
  "pinnedByName",
  "pinnedByUserId",
  "policyId",
  "policyName",
  "requestedByUserId",
  "retention",
  "retentionMinutes",
  "s3",
  "scheduleId",
  "scheduleName",
  "schemaOnly",
  "scanRoot",
  "secretKey",
  "sourceShard",
  "sourceShardName",
  "sourceShards",
  "startedAt",
  "targetDatabase",
  "targetReplicaNumber",
  "targetShard",
  "targetShardName",
  "targetShardNumber",
  "targetShards",
  "targetTable",
  "updatedAt",
  "userName",
  "password",
  "warning"
]);

const nullablePropertyPaths = new Set([
  "BackupSettingsPreviewRequest.clusterId"
]);

const optionalProperties = new Set([
  "InitiateRestoreRequest.database",
  "InitiateRestoreRequest.table",
  "InitiateRestoreRequest.tables",
  "ManualBackupRequest.clickHouseBackupSettings",
  "InitiateRestoreRequest.clickHouseRestoreSettings"
]);

const scalarSettingsProperties = new Set([
  "clickHouseBackupSettings",
  "clickHouseRestoreSettings",
  "settings"
]);

const scalarSettingProperties = new Set([
  "ClickHouseSettingSourceDto.value"
]);

const header = `/* Generated from Chobo OpenAPI. Regenerate with npm run generate:api. */\n\n`;
const aliases = [
  `export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };`,
  `export type ClickHouseSettingValue = string | number | boolean;`,
  `export interface PagedResultDto<T> { items: T[]; offset: number; limit: number; totalCount: number; }`
];
const declarations = [];

for (const name of schemaNames) {
  const schema = schemas[name];
  if (Array.isArray(schema.enum)) {
    declarations.push(`export type ${name} = ${schema.enum.map((value) => JSON.stringify(value)).join(" | ")};`);
    continue;
  }

  if (isPagedResult(name, schema)) {
    const itemType = typeName(schema.properties.items.items);
    declarations.push(`export interface ${name} { items: ${itemType}[]; offset: number; limit: number; totalCount: number; }`);
    continue;
  }

  if (schema.type === "object" || schema.properties) {
    declarations.push(renderInterface(name, schema));
    continue;
  }

  declarations.push(`export type ${name} = ${typeName(schema)};`);
}

declarations.push(`export const openApiSchemaNames = ${JSON.stringify(schemaNames, null, 2)} as const;`);
writeFileSync(generatedPath, `${header}${aliases.join("\n")}\n\n${declarations.join("\n")}\n`);
console.log(`Generated ${generatedPath} from ${snapshotPath}.`);

function renderInterface(name, schema) {
  const properties = schema.properties ?? {};
  const fields = Object.entries(properties).map(([propertyName, propertySchema]) => {
    const optional = isOptionalProperty(name, propertyName, propertySchema) ? "?" : "";
    return `${propertyName}${optional}: ${propertyTypeName(name, propertyName, propertySchema)};`;
  });
  return `export interface ${name} { ${fields.join(" ")} }`;
}

function isOptionalProperty(schemaName, propertyName, schema) {
  return optionalProperties.has(`${schemaName}.${propertyName}`) || (schema?.nullable === true && (nullableProperties.has(propertyName) || nullablePropertyPaths.has(`${schemaName}.${propertyName}`)));
}

function propertyTypeName(schemaName, propertyName, schema) {
  const nullable = schema?.nullable === true && (nullableProperties.has(propertyName) || nullablePropertyPaths.has(`${schemaName}.${propertyName}`));
  if (scalarSettingProperties.has(`${schemaName}.${propertyName}`)) return withNullable("ClickHouseSettingValue", nullable);
  if (scalarSettingsProperties.has(propertyName) && schema?.additionalProperties) return withNullable("Record<string, ClickHouseSettingValue>", nullable);
  return typeName(schema, nullable);
}

function typeName(schema, nullable = false) {
  if (!schema) return "unknown";
  if (schema.$ref) return schema.$ref.split("/").pop();

  const union = schema.oneOf ?? schema.anyOf;
  if (Array.isArray(union)) {
    const types = union.map((item) => typeName(item)).filter((value) => value !== "null");
    const unique = [...new Set(types)];
    return withNullable(unique.length === 1 ? unique[0] : unique.join(" | "), nullable || union.some((item) => item.type === "null"));
  }

  if (Array.isArray(schema.enum)) {
    return withNullable(schema.enum.map((value) => JSON.stringify(value)).join(" | "), nullable);
  }

  if (schema.type === "array") return withNullable(`${typeName(schema.items)}[]`, nullable);
  if (schema.type === "integer" || schema.type === "number") return withNullable("number", nullable);
  if (schema.type === "boolean") return withNullable("boolean", nullable);
  if (schema.type === "string") return withNullable("string", nullable);
  if (schema.type === "object") {
    if (schema.additionalProperties) return withNullable(`Record<string, ${typeName(schema.additionalProperties)}>`, nullable);
    return withNullable("JsonValue", nullable);
  }

  return withNullable("JsonValue", nullable);
}

function withNullable(type, nullable) {
  return nullable && !type.includes("null") ? `${type} | null` : type;
}

function isPagedResult(name, schema) {
  return name.endsWith("PagedResultDto") && schema.properties?.items?.type === "array";
}