import { PipelineDocument, newStep } from "./pipeline.js";
import { renderFields } from "./renderer.js";

const schema = JSON.parse(document.getElementById("action-schema").textContent);
const actionMap = new Map(schema.actions.map(action => [action.type, action]));
const state = {
  model: PipelineDocument.empty(),
  selected: -1,
  undo: [],
  redo: [],
  pendingEditStart: null,
  previewDirty: false
};

const elements = {
  notice: document.getElementById("notice"),
  readonly: document.getElementById("readonly-warning"),
  steps: document.getElementById("step-list"),
  editor: document.getElementById("editor"),
  empty: document.getElementById("empty-editor"),
  yaml: document.getElementById("yaml-preview"),
  variables: document.getElementById("variables-editor"),
  previewStatus: document.getElementById("preview-status")
};

function pathKey(path) { return JSON.stringify(path); }

function notify(message, error = false) {
  elements.notice.textContent = message;
  elements.notice.className = `mb-3 rounded border p-3 text-sm ${error ? "border-red-300 bg-red-50 text-red-900" : "border-emerald-300 bg-emerald-50 text-emerald-900"}`;
  elements.notice.hidden = false;
}

function beforeMutation() {
  state.undo.push(state.model.yaml);
  if (state.undo.length > 100) state.undo.shift();
  state.redo = [];
}

function updatePreviewStatus() {
  elements.previewStatus.textContent = state.previewDirty ? "Modifiche non caricate" : "Anteprima aggiornata";
  elements.previewStatus.className = state.previewDirty ? "text-xs text-amber-700" : "text-xs text-slate-500";
}

function updateHistoryButtons() {
  document.getElementById("undo").disabled = !state.model.editable || state.undo.length === 0;
  document.getElementById("redo").disabled = !state.model.editable || state.redo.length === 0;
}

function commitPendingEdit() {
  if (state.pendingEditStart === null) return;
  if (state.pendingEditStart !== state.model.yaml) {
    state.undo.push(state.pendingEditStart);
    if (state.undo.length > 100) state.undo.shift();
    state.redo = [];
  }
  state.pendingEditStart = null;
  updateHistoryButtons();
}

function restoreFocus() {
  const active = document.activeElement;
  if (!active?.dataset.fieldPath) return null;
  return {
    path: active.dataset.fieldPath,
    start: active.selectionStart,
    end: active.selectionEnd
  };
}

function preserveFocus(focus) {
  if (!focus) return;
  const replacement = [...document.querySelectorAll("[data-field-path]")].find(input => input.dataset.fieldPath === focus.path);
  replacement?.focus();
  if (replacement && typeof focus.start === "number" && replacement.setSelectionRange) {
    replacement.setSelectionRange(focus.start, focus.end);
  }
}

function mutate(change, message) {
  const focus = restoreFocus();
  try {
    commitPendingEdit();
    beforeMutation();
    change();
    state.previewDirty = true;
    render();
    preserveFocus(focus);
    if (message) notify(message);
  } catch (error) {
    state.undo.pop();
    notify(error.message, true);
  }
}

function setValue(path, value, mode) {
  if (mode === "begin") {
    if (state.pendingEditStart === null) state.pendingEditStart = state.model.yaml;
    return;
  }
  if (mode === "commit") {
    commitPendingEdit();
    return;
  }
  if (mode === "draft") {
    try {
      if (state.pendingEditStart === null) state.pendingEditStart = state.model.yaml;
      state.model.set(path, value);
      state.previewDirty = true;
      updatePreviewStatus();
    } catch (error) {
      notify(error.message, true);
    }
    return;
  }
  mutate(() => state.model.set(path, value));
}
function deleteValue(path) { mutate(() => state.model.delete(path)); }

function makeButton(text, title, action, className = "rounded border px-2 py-1 text-sm hover:bg-slate-50") {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = text;
  button.title = title;
  button.className = className;
  button.addEventListener("click", action);
  return button;
}

function renderStepList() {
  elements.steps.replaceChildren();
  for (const [index, step] of state.model.steps.entries()) {
    const item = document.createElement("li");
    const card = document.createElement("div");
    card.className = "step-card grid grid-cols-[minmax(0,1fr)_auto] items-center gap-1 rounded border p-2";
    card.setAttribute("aria-current", String(index === state.selected));
    const select = makeButton("", `Modifica step ${step.id ?? index + 1}`, () => {
      state.selected = index;
      render();
    }, "min-w-0 text-left");
    select.className = "min-w-0 text-left";
    const title = document.createElement("span");
    title.className = "block truncate text-sm font-medium";
    title.textContent = step.name || step.id || `Step ${index + 1}`;
    const subtitle = document.createElement("span");
    subtitle.className = "block truncate text-xs text-slate-500";
    subtitle.textContent = step.type ?? "Tipo non specificato";
    select.append(title, subtitle);
    const controls = document.createElement("div");
    controls.className = "flex gap-1";
    const up = makeButton("↑", "Sposta step in alto", () => {
      if (index > 0) mutate(() => {
        state.model.moveStep(index, index - 1);
        state.selected = index - 1;
      });
    }, "rounded px-1 text-sm hover:bg-slate-100");
    up.disabled = index === 0;
    const down = makeButton("↓", "Sposta step in basso", () => {
      if (index < state.model.steps.length - 1) mutate(() => {
        state.model.moveStep(index, index + 1);
        state.selected = index + 1;
      });
    }, "rounded px-1 text-sm hover:bg-slate-100");
    down.disabled = index === state.model.steps.length - 1;
    controls.append(up, down);
    card.append(select, controls);
    item.append(card);
    elements.steps.append(item);
  }
}

function renderEditor() {
  elements.editor.replaceChildren();
  const step = state.model.steps[state.selected];
  if (!step) {
    elements.editor.classList.add("hidden");
    elements.empty.classList.remove("hidden");
    return;
  }
  elements.editor.classList.remove("hidden");
  elements.empty.classList.add("hidden");

  const title = document.createElement("div");
  title.className = "mb-4 flex flex-wrap items-start justify-between gap-3";
  const heading = document.createElement("div");
  const headingText = document.createElement("h2");
  headingText.className = "font-semibold";
  headingText.textContent = step.name || step.id || `Step ${state.selected + 1}`;
  const description = document.createElement("p");
  description.className = "text-xs text-slate-500";
  description.textContent = actionMap.get(step.type)?.description ?? "Action non presente nel catalogo.";
  heading.append(headingText, description);
  const actions = document.createElement("div");
  actions.className = "flex flex-wrap gap-2";
  actions.append(
    makeButton("Duplica", "Duplica step", () => mutate(() => {
      const clone = structuredClone(step);
      clone.id = uniqueId(`${step.id ?? "step"}-copy`);
      state.selected = state.model.addStep(clone);
    })),
    makeButton("Elimina", "Elimina step", () => mutate(() => {
      state.model.removeStep(state.selected);
      state.selected = Math.min(state.selected, state.model.steps.length - 1);
    }), "rounded border border-red-300 px-2 py-1 text-sm text-red-700 hover:bg-red-50")
  );
  title.append(heading, actions);
  elements.editor.append(title);

  const fields = document.createElement("div");
  fields.className = "grid gap-x-4 sm:grid-cols-2";
  fields.append(
    scalarControl("id", "Identificatore", step.id ?? "", ["pipeline", state.selected, "id"], "text"),
    scalarControl("name", "Nome visualizzato", step.name ?? "", ["pipeline", state.selected, "name"], "text")
  );
  const actionField = document.createElement("label");
  actionField.className = "field py-2";
  const actionLabel = document.createElement("span");
  actionLabel.className = "field-label";
  actionLabel.textContent = "Action";
  const actionSelect = document.createElement("select");
  actionSelect.className = "control";
  for (const action of schema.actions) {
    const option = document.createElement("option");
    option.value = action.type;
    option.textContent = action.type;
    actionSelect.append(option);
  }
  actionSelect.value = actionMap.has(step.type) ? step.type : "";
  actionSelect.setAttribute("aria-label", "Action");
  if (!actionMap.has(step.type)) {
    const unknown = document.createElement("option");
    unknown.value = step.type ?? "";
    unknown.textContent = `${step.type ?? "(manca type)"} (sconosciuta)`;
    actionSelect.append(unknown);
    actionSelect.value = step.type ?? "";
  }
  actionSelect.dataset.fieldPath = pathKey(["pipeline", state.selected, "type"]);
  actionSelect.addEventListener("change", () => mutate(() => {
    state.model.set(["pipeline", state.selected, "type"], actionSelect.value);
  }));
  actionField.append(actionLabel, actionSelect);
  fields.append(actionField);

  const enabled = document.createElement("label");
  enabled.className = "field py-2";
  const enabledLabel = document.createElement("span");
  enabledLabel.className = "field-label";
  enabledLabel.textContent = "Abilitato";
  const checkbox = document.createElement("input");
  checkbox.type = "checkbox";
  checkbox.checked = step.enabled !== false;
  checkbox.dataset.fieldPath = pathKey(["pipeline", state.selected, "enabled"]);
  checkbox.addEventListener("change", () => setValue(["pipeline", state.selected, "enabled"], checkbox.checked));
  enabled.append(enabledLabel, checkbox);
  fields.append(enabled);
  elements.editor.append(fields);

  const configHeader = document.createElement("h3");
  configHeader.className = "mb-2 mt-4 border-t pt-3 font-semibold";
  configHeader.textContent = "Configurazione action";
  elements.editor.append(configHeader);
  const selectedAction = actionMap.get(step.type);
  if (!selectedAction) {
    const hint = document.createElement("p");
    hint.className = "text-sm text-amber-800";
    hint.textContent = "Action sconosciuta: i campi config originali sono conservati.";
    elements.editor.append(hint);
  } else if (step.config === undefined || step.config === null) {
    elements.editor.append(makeButton("Aggiungi configurazione", "Crea la mappa config", () => setValue(["pipeline", state.selected, "config"], {})));
  } else {
    const configFields = document.createElement("div");
    configFields.className = "grid gap-x-4 sm:grid-cols-2";
    renderFields(configFields, selectedAction.schema, step.config, ["pipeline", state.selected, "config"], setValue, deleteValue);
    elements.editor.append(configFields);
  }

  const utility = document.createElement("div");
  utility.className = "mt-4 flex flex-wrap gap-2 border-t pt-3";
  utility.append(
    makeButton("↑ Sposta", "Sposta step sopra", () => {
      if (state.selected > 0) mutate(() => {
        state.model.moveStep(state.selected, state.selected - 1);
        state.selected--;
      });
    }),
    makeButton("↓ Sposta", "Sposta step sotto", () => {
      if (state.selected < state.model.steps.length - 1) mutate(() => {
        state.model.moveStep(state.selected, state.selected + 1);
        state.selected++;
      });
    })
  );
  elements.editor.append(utility);
}

function scalarControl(key, labelText, value, path, type) {
  const label = document.createElement("label");
  label.className = "field py-2";
  const title = document.createElement("span");
  title.className = "field-label";
  title.textContent = labelText;
  const input = document.createElement("input");
  input.type = type;
  input.value = value;
  input.className = "control";
  input.dataset.fieldPath = pathKey(path);
  input.addEventListener("focus", () => setValue(path, undefined, "begin"));
  input.addEventListener("input", () => setValue(path, input.value, "draft"));
  input.addEventListener("change", () => setValue(path, undefined, "commit"));
  label.append(title, input);
  return label;
}

function uniqueId(base) {
  const ids = new Set(state.model.steps.map(step => step.id));
  let candidate = base;
  let suffix = 2;
  while (ids.has(candidate)) candidate = `${base}-${suffix++}`;
  return candidate;
}

function renderVariables() {
  elements.variables.replaceChildren();
  const variables = state.model.variables;
  for (const [key, value] of Object.entries(variables)) {
    const row = document.createElement("div");
    row.className = "grid grid-cols-[minmax(0,1fr)_minmax(0,2fr)_auto] gap-2";
    const name = document.createElement("input");
    name.className = "control";
    name.value = key;
    name.setAttribute("aria-label", "Nome variabile");
    name.dataset.fieldPath = pathKey(["variables", key, "key"]);
    name.autocomplete = "off";
    name.addEventListener("change", () => mutate(() => state.model.renameVariable(key, name.value)));
    const input = document.createElement("input");
    input.className = "control";
    input.type = "password";
    input.value = String(value ?? "");
    input.setAttribute("aria-label", `Valore ${key}`);
    input.autocomplete = "new-password";
    input.dataset.fieldPath = pathKey(["variables", key]);
    input.addEventListener("focus", () => setValue(["variables", key], undefined, "begin"));
    input.addEventListener("input", () => setValue(["variables", key], input.value, "draft"));
    input.addEventListener("change", () => setValue(["variables", key], undefined, "commit"));
    row.append(name, input, makeButton("×", `Rimuovi variabile ${key}`, () => deleteValue(["variables", key])));
    elements.variables.append(row);
  }
}

function renderReadOnlyState() {
  const reason = state.model.readOnlyReason;
  elements.readonly.textContent = reason ? `Modifica disabilitata per evitare perdita di dati: ${reason}. Il download conserva il documento originale.` : "";
  elements.readonly.hidden = !reason;
  for (const control of document.querySelectorAll("button, input, select")) {
    if (control.id === "import-file" || control.id === "download" || control.id === "yaml-details") continue;
    control.disabled = Boolean(reason);
  }
  document.getElementById("add-step").disabled = Boolean(reason);
  document.getElementById("add-variable").disabled = Boolean(reason);
  updateHistoryButtons();
  document.getElementById("refresh-preview").disabled = Boolean(reason);
}

function render() {
  renderStepList();
  renderEditor();
  renderVariables();
  updatePreviewStatus();
  renderReadOnlyState();
}

function loadPreview() {
  commitPendingEdit();
  elements.yaml.textContent = state.model.yaml;
  state.previewDirty = false;
  render();
}

function changeAction(type) {
  if (!state.model.editable) return;
  mutate(() => {
    const step = newStep(type, state.model.steps.length);
    step.id = uniqueId(step.id);
    const index = state.model.addStep(step);
    state.selected = index;
  });
}

function undo() {
  commitPendingEdit();
  if (!state.model.editable || !state.undo.length) return;
  state.redo.push(state.model.yaml);
  state.model = PipelineDocument.parse(state.undo.pop());
  state.selected = Math.min(state.selected, state.model.steps.length - 1);
  render();
}

function redo() {
  commitPendingEdit();
  if (!state.model.editable || !state.redo.length) return;
  state.undo.push(state.model.yaml);
  state.model = PipelineDocument.parse(state.redo.pop());
  state.selected = Math.min(state.selected, state.model.steps.length - 1);
  render();
}

document.getElementById("add-step").addEventListener("click", () => {
  const action = schema.actions[0];
  if (action) changeAction(action.type);
});
document.getElementById("add-variable").addEventListener("click", () => {
  const existing = new Set(Object.keys(state.model.variables));
  let name = "NewVariable";
  let count = 2;
  while (existing.has(name)) name = `NewVariable${count++}`;
  mutate(() => state.model.set(["variables", name], ""));
});
document.getElementById("undo").addEventListener("click", undo);
document.getElementById("redo").addEventListener("click", redo);
document.getElementById("refresh-preview").addEventListener("click", loadPreview);
document.getElementById("download").addEventListener("click", () => {
  const blob = new Blob([state.model.yaml], { type: "application/yaml;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = "pipeline.yaml";
  anchor.click();
  URL.revokeObjectURL(url);
});
document.getElementById("import-file").addEventListener("change", async event => {
  const [file] = event.target.files ?? [];
  if (!file) return;
  try {
    state.model = PipelineDocument.parse(await file.text());
    state.undo = [];
    state.redo = [];
    state.pendingEditStart = null;
    state.previewDirty = true;
    state.selected = state.model.steps.length ? 0 : -1;
    notify("YAML importato. I campi non riconosciuti e i commenti sono mantenuti nel documento.");
    render();
    loadPreview();
  } catch (error) {
    notify(`Importazione non riuscita: ${error.message}`, true);
  } finally {
    event.target.value = "";
  }
});

document.addEventListener("keydown", event => {
  if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== "z") return;
  event.preventDefault();
  if (event.shiftKey) redo();
  else undo();
});

render();
