import test from "node:test";
import assert from "node:assert/strict";
import { PipelineDocument, newStep } from "../src/pipeline.js";

test("document edits retain comments and unknown values across YAML export/import", () => {
  const source = [
    "# pipeline comment",
    "variables:",
    "  Root: 'D:\\\\data' # keep this comment",
    "customRoot: retained",
    "pipeline:",
    "  - id: one # step comment",
    "    type: HOSTS",
    "    config:",
    "      vendorField: preserved",
    "  - id: two",
    "    type: ROBOCOPY",
    ""
  ].join("\n");
  const model = PipelineDocument.parse(source);
  model.set(["pipeline", 0, "enabled"], false);
  model.moveStep(1, 0);

  const exported = model.yaml;
  assert.match(exported, /# pipeline comment/);
  assert.match(exported, /# keep this comment/);
  assert.match(exported, /# step comment/);
  assert.match(exported, /customRoot: retained/);
  assert.match(exported, /vendorField: preserved/);
  const reimported = PipelineDocument.parse(exported);
  assert.equal(reimported.steps[1].enabled, false);
  assert.equal(reimported.steps[1].config.vendorField, "preserved");
});

test("omitted, false, zero, and empty string remain distinct", () => {
  const model = PipelineDocument.parse("variables: {}\npipeline:\n  - id: sample\n    type: HOSTS\n    config:\n      empty: ''\n      zero: 0\n      disabled: false\n");
  const config = model.steps[0].config;
  assert.equal(Object.hasOwn(config, "missing"), false);
  assert.equal(config.disabled, false);
  assert.equal(config.zero, 0);
  assert.equal(config.empty, "");
  model.set(["pipeline", 0, "config", "missing"], "");
  assert.equal(Object.hasOwn(model.steps[0].config, "missing"), true);
  model.delete(["pipeline", 0, "config", "zero"]);
  assert.equal(Object.hasOwn(model.steps[0].config, "zero"), false);
});

test("invalid or structurally uneditable YAML is not mutated", () => {
  const model = PipelineDocument.parse("pipeline: not-a-list\n");
  assert.equal(model.editable, false);
  assert.match(model.readOnlyReason, /pipeline deve essere una lista/);
  assert.throws(() => model.set(["pipeline", 0], {}), /sola lettura/);
  assert.match(model.yaml, /pipeline: not-a-list/);
});

test("new YAML sequence steps are editable as nested mapping nodes", () => {
  const model = PipelineDocument.empty();
  const index = model.addStep(newStep("REQUIRED-FOLDERS", 0));
  model.set(["pipeline", index, "config", "paths"], ["D:\\data"]);
  model.set(["pipeline", index, "enabled"], false);
  assert.deepEqual(model.steps[0].config.paths, ["D:\\data"]);
  assert.equal(model.steps[0].enabled, false);
});

test("updating maps and removing list items keeps comments on retained YAML nodes", () => {
  const model = PipelineDocument.parse([
    "pipeline:",
    "  - id: copy",
    "    type: HOSTS",
    "    config:",
    "      paths:",
    "        - first # keep first path comment",
    "        - second # keep second path comment",
    "      credentials:",
    "        account: secret # keep credential comment",
    ""
  ].join("\n"));
  model.set(["pipeline", 0, "config", "paths"], ["second"]);
  model.set(["pipeline", 0, "config", "credentials"], { account: "updated", other: "" });
  assert.match(model.yaml, /second # keep second path comment/);
  assert.match(model.yaml, /account: updated # keep credential comment/);
});

test("renaming variables changes the YAML key and keeps block mapping style", () => {
  const model = PipelineDocument.empty();
  model.set(["variables", "NewVariable"], "TEBTCH03");
  model.renameVariable("NewVariable", "SourceServer");
  assert.deepEqual(model.variables, { SourceServer: "TEBTCH03" });
  assert.match(model.yaml, /variables:\r?\n  SourceServer: TEBTCH03/);
  assert.doesNotMatch(model.yaml, /variables: \{/);
});

test("variable rename rejects blank and duplicate names without changing the model", () => {
  const model = PipelineDocument.parse("variables:\n  SourceServer: first\n  TargetServer: second\npipeline: []\n");
  assert.throws(() => model.renameVariable("SourceServer", " "), /non può essere vuoto/);
  assert.throws(() => model.renameVariable("SourceServer", "TargetServer"), /Esiste già/);
  assert.deepEqual(model.variables, { SourceServer: "first", TargetServer: "second" });
});

test("variables can be added when the imported pipeline has no variables section", () => {
  const model = PipelineDocument.parse("pipeline: []\n");
  model.set(["variables", "SourceServer"], "TEBTCH03");
  assert.deepEqual(model.variables, { SourceServer: "TEBTCH03" });
  assert.match(model.yaml, /variables:\r?\n  SourceServer: TEBTCH03/);
});
