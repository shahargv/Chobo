import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const snapshotPath = resolve(root, "openapi/chobo.v1.json");
const generatedPath = resolve(root, "src/api/generated.ts");
const snapshot = JSON.parse(readFileSync(snapshotPath, "utf8"));
const schemaNames = Object.keys(snapshot.components?.schemas ?? {});

const header = `/* Generated from Chobo OpenAPI. Regenerate with npm run generate:api. */\n`;
const current = readFileSync(generatedPath, "utf8");
if (!current.startsWith(header)) {
  throw new Error("Generated API file header is missing.");
}

const marker = `\nexport const openApiSchemaNames = `;
const base = current.includes(marker) ? current.slice(0, current.indexOf(marker)) : current.trimEnd();
writeFileSync(generatedPath, `${base}${marker}${JSON.stringify(schemaNames.sort(), null, 2)} as const;\n`);
console.log(`Updated ${generatedPath} from ${snapshotPath}.`);
