import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const catalog = JSON.parse(await readFile(path.join(root, "dist", "actions.schema.json"), "utf8"));

test("the schema covers every catalog action with descriptions and structured config", () => {
  const expected = [
    "REQUIRED-FOLDERS", "FILE-BATCH-PROCESS", "HOSTS", "ROBOCOPY",
    "EXECUTE-PROCESS", "MSI-INSTALL", "CREATE-SERVICES", "CREATE-TASKS"
  ];
  assert.deepEqual(catalog.actions.map(action => action.type).sort(), expected.sort());
  for (const action of catalog.actions) {
    assert.ok(action.description.trim(), `${action.type} has a description`);
    assert.equal(action.schema.type, "object");
    assert.ok(Object.keys(action.schema.properties).length > 0);
    for (const [name, property] of Object.entries(action.schema.properties)) {
      assert.ok(property.description?.trim(), `${action.type}.${name} has a description`);
    }
  }
});

test("schema includes variants, required dependencies, defaults, limits, enums, and sensitive fields", () => {
  const folders = catalog.actions.find(action => action.type === "REQUIRED-FOLDERS").schema.properties.paths;
  assert.equal(folders.items.oneOf.length, 2);
  const pathConfig = folders.items.oneOf.find(branch => branch.type === "object");
  assert.deepEqual(pathConfig.required, ["path"]);
  assert.deepEqual(pathConfig.properties.source["x-ui"].requiredWhen, { property: "depth", notEqual: "0" });

  const robocopy = catalog.actions.find(action => action.type === "ROBOCOPY").schema.properties;
  assert.equal(robocopy.threads.minimum, 1);
  assert.equal(robocopy.threads.maximum, 128);
  assert.equal(robocopy.mirror.default, true);

  const filter = catalog.actions.find(action => action.type === "FILE-BATCH-PROCESS").schema.properties.filter;
  assert.deepEqual(filter.properties.matchType.enum, ["Auto", "Regex", "Fixed", "Glob"]);
  const credentials = catalog.actions.find(action => action.type === "CREATE-SERVICES").schema.properties.credentials;
  assert.equal(credentials["x-ui"].sensitive, true);
});
