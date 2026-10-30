const OMITTED = Symbol("omitted");

function node(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function labelFor(schema, name) {
  const label = node("span", "field-label", name);
  return label;
}

export function renderFields(container, schema, value, path, onSet, onDelete) {
  for (const [name, fieldSchema] of Object.entries(schema?.properties ?? {})) {
    const present = value != null && Object.hasOwn(value, name);
    const current = present ? value[name] : OMITTED;
    const fieldPath = [...path, name];
    const condition = fieldSchema["x-ui"]?.requiredWhen;
    const dependencyValue = condition ? (value?.[condition.property] ?? schema.properties?.[condition.property]?.default) : undefined;
    const conditionRequired = condition && dependencyValue !== undefined && String(dependencyValue) !== condition.notEqual;

    if (fieldSchema.oneOf) {
      renderVariant(container, name, fieldSchema, current, fieldPath, onSet, onDelete, conditionRequired);
    } else if (fieldSchema.type === "array") {
      renderArray(container, name, fieldSchema, current, fieldPath, onSet, onDelete, conditionRequired);
    } else if (fieldSchema.type === "object") {
      renderObject(container, name, fieldSchema, current, fieldPath, onSet, onDelete, conditionRequired);
    } else {
      renderScalar(container, name, fieldSchema, current, fieldPath, onSet, onDelete, conditionRequired);
    }
  }
}

function wrapper(name, schema, current, path, onDelete, conditionRequired = false) {
  const field = node("div", "field py-2");
  const label = labelFor(schema, name);
  if (conditionRequired) label.textContent += " · richiesto";
  if (schema["x-ui"]?.sensitive) label.textContent += " · sensibile";
  field.append(label);
  if (schema.description) field.append(node("span", "field-description", schema.description));
  if (current === OMITTED) field.append(node("span", "field-status", statusText(schema)));
  else {
    const remove = node("button", "w-fit rounded border px-2 py-1 text-xs text-slate-700 hover:bg-slate-50", "Rimuovi campo");
    remove.type = "button";
    remove.addEventListener("click", () => onDelete(path));
    field.append(remove);
  }
  return field;
}

function statusText(schema) {
  return schema.default === undefined ? "Ommesso" : `Ommesso · default visualizzato: ${JSON.stringify(schema.default)}`;
}

function renderScalar(container, name, schema, current, path, onSet, onDelete, conditionRequired = false) {
  const field = wrapper(name, schema, current, path, onDelete, conditionRequired);
  if (current !== OMITTED && !validScalar(schema.type, current)) {
    field.append(node("span", "field-status", `Valore YAML non compatibile con il tipo ${schema.type}; viene conservato finché non lo correggi.`));
    container.append(field);
    return;
  }
  if (schema.type === "boolean") {
    const row = node("label", "flex items-center gap-2 text-sm");
    const input = node("input", "h-4 w-4");
    input.type = "checkbox";
    input.setAttribute("aria-label", name);
    input.dataset.fieldPath = JSON.stringify(path);
    input.checked = current !== OMITTED && current === true;
    input.indeterminate = current === OMITTED;
    input.addEventListener("change", () => onSet(path, input.checked));
    row.append(input, node("span", "", current === OMITTED ? `Ommesso (default: ${schema.default ?? "nessuno"})` : String(current)));
    field.append(row);
  } else if (schema.enum) {
    const select = node("select", "control");
    select.setAttribute("aria-label", name);
    select.dataset.fieldPath = JSON.stringify(path);
    const omitted = node("option", "", current === OMITTED ? `Ommesso${schema.default === undefined ? "" : ` · default: ${schema.default}`}` : "Seleziona");
    omitted.value = "";
    omitted.disabled = current === OMITTED;
    select.append(omitted);
    for (const option of schema.enum) {
      const element = node("option", "", option);
      element.value = option;
      select.append(element);
    }
    select.value = current === OMITTED ? "" : String(current);
    select.addEventListener("change", () => onSet(path, select.value));
    field.append(select);
  } else {
    const input = node(schema.type === "string" && schema["x-ui"]?.widget === "textarea" ? "textarea" : "input", "control");
    input.setAttribute("aria-label", name);
    input.dataset.fieldPath = JSON.stringify(path);
    input.type = schema.type === "integer" || schema.type === "number" ? "number" :
      (schema["x-ui"]?.sensitive ? "password" : "text");
    if (input.type === "number") {
      input.step = schema.type === "integer" ? "1" : "any";
      if (schema.minimum !== undefined) input.min = String(schema.minimum);
      if (schema.maximum !== undefined) input.max = String(schema.maximum);
    }
    input.value = current === OMITTED ? "" : String(current);
    if (current === OMITTED && schema.default !== undefined) input.placeholder = `Default: ${schema.default}`;
    input.autocomplete = schema["x-ui"]?.sensitive ? "new-password" : "off";
    input.addEventListener("focus", () => onSet(path, undefined, "begin"));
    input.addEventListener("input", () => {
      if (schema.type === "string") onSet(path, input.value, "draft");
      else if (input.value !== "" && Number.isFinite(input.valueAsNumber)) {
        onSet(path, schema.type === "integer" ? Math.trunc(input.valueAsNumber) : input.valueAsNumber, "draft");
      }
    });
    input.addEventListener("change", () => onSet(path, undefined, "commit"));
    field.append(input);
  }
  container.append(field);
}

function renderObject(container, name, schema, current, path, onSet, onDelete, conditionRequired = false) {
  const field = wrapper(name, schema, current, path, onDelete, conditionRequired);
  if (current === OMITTED) {
    const add = node("button", "w-fit rounded border px-2 py-1 text-sm hover:bg-slate-50", `Aggiungi ${name}`);
    add.type = "button";
    add.addEventListener("click", () => onSet(path, {}));
    field.append(add);
  } else if (current === null || typeof current !== "object" || Array.isArray(current)) {
    field.append(node("span", "field-status", "Valore YAML non compatibile con un oggetto; viene conservato."));
    field.append(makeReplacementButton(name, path, onSet, {}));
  } else {
    const nested = node("div", "tree");
    const objectSchema = schema.additionalProperties ? { properties: {} } : schema;
    if (schema.additionalProperties) {
      renderMap(nested, name, schema, current, path, onSet, onDelete);
    } else {
      renderFields(nested, objectSchema, current ?? {}, path, onSet, onDelete);
    }
    field.append(nested);
  }
  container.append(field);
}

function renderMap(container, name, schema, current, path, onSet, onDelete) {
  for (const [key, value] of Object.entries(current ?? {})) {
    const row = node("div", "grid grid-cols-[minmax(0,1fr)_auto] gap-2 py-1");
    const keyInput = node("input", "control");
    keyInput.dataset.fieldPath = JSON.stringify([...path, key, "key"]);
    keyInput.value = key;
    keyInput.setAttribute("aria-label", `${name} chiave`);
    const valueInput = node("input", "control");
    valueInput.dataset.fieldPath = JSON.stringify([...path, key]);
    valueInput.type = schema["x-ui"]?.sensitive ? "password" : "text";
    valueInput.value = String(value ?? "");
    valueInput.setAttribute("aria-label", `${name} ${key}`);
    valueInput.autocomplete = "new-password";
    valueInput.addEventListener("focus", () => onSet(path, undefined, "begin"));
    const update = () => {
      const entries = { ...current };
      if (keyInput.value !== key && Object.hasOwn(entries, keyInput.value)) {
        keyInput.setCustomValidity("Chiave già presente");
        return;
      }
      keyInput.setCustomValidity("");
      delete entries[key];
      entries[keyInput.value] = valueInput.value;
      onSet(path, entries);
    };
    keyInput.addEventListener("change", update);
    valueInput.addEventListener("input", () => onSet(path, { ...current, [key]: valueInput.value }, "draft"));
    valueInput.addEventListener("change", () => onSet(path, undefined, "commit"));
    const remove = node("button", "rounded border px-2 text-sm", "×");
    remove.type = "button";
    remove.setAttribute("aria-label", `Rimuovi ${key}`);
    remove.addEventListener("click", () => {
      const entries = { ...current };
      delete entries[key];
      onSet(path, entries);
    });
    row.append(keyInput, valueInput, remove);
    container.append(row);
  }
  const add = node("button", "mt-2 rounded border px-2 py-1 text-sm hover:bg-slate-50", "Aggiungi elemento");
  add.type = "button";
  add.addEventListener("click", () => onSet(path, { ...current, [`elemento-${Object.keys(current ?? {}).length + 1}`]: "" }));
  container.append(add);
}

function renderArray(container, name, schema, current, path, onSet, onDelete, conditionRequired = false) {
  const field = wrapper(name, schema, current, path, onDelete, conditionRequired);
  const isArray = Array.isArray(current);
  if (current !== OMITTED && !isArray) {
    field.append(node("span", "field-status", "Valore YAML non compatibile con una lista; viene conservato."));
    field.append(makeReplacementButton(name, path, onSet, []));
  }
  if (isArray) {
    for (const [index, item] of current.entries()) {
      const row = node("div", "my-2 rounded border p-2");
      const itemSchema = schema.items ?? { type: "string" };
      if (itemSchema.oneOf) renderVariant(row, `${name}[${index}]`, itemSchema, item, [...path, index], onSet, onDelete);
      else if (itemSchema.type === "object") renderFields(row, itemSchema, item ?? {}, [...path, index], onSet, onDelete);
      else renderScalar(row, `${name}[${index}]`, itemSchema, item, [...path, index], onSet, onDelete);
      const remove = node("button", "rounded border px-2 py-1 text-xs", "Rimuovi elemento");
      remove.type = "button";
      remove.addEventListener("click", () => onSet(path, current.filter((_, itemIndex) => itemIndex !== index)));
      row.append(remove);
      field.append(row);
    }
  }
  if (current === OMITTED || isArray) {
    const add = node("button", "w-fit rounded border px-2 py-1 text-sm hover:bg-slate-50", `Aggiungi elemento a ${name}`);
    add.type = "button";
    add.addEventListener("click", () => {
      const itemSchema = schema.items ?? { type: "string" };
      const initial = itemSchema.oneOf ? "" : itemSchema.type === "object" ? {} :
        itemSchema.type === "boolean" ? false : itemSchema.type === "integer" || itemSchema.type === "number" ? 0 : "";
      onSet(path, [...(isArray ? current : []), initial]);
    });
    field.append(add);
  }
  container.append(field);
}

function renderVariant(container, name, schema, current, path, onSet, onDelete, conditionRequired = false) {
  const field = wrapper(name, schema, current, path, onDelete, conditionRequired);
  const options = schema.oneOf.map(branch => branch.type ?? (branch.properties ? "object" : "string"));
  const validVariant = typeof current === "string" || (current !== null && typeof current === "object" && !Array.isArray(current));
  const selected = current === OMITTED || !validVariant ? "" : (typeof current === "string" ? "string" : "object");
  if (current !== OMITTED && !validVariant) {
    field.append(node("span", "field-status", "Valore YAML non compatibile con le varianti supportate; viene conservato."));
  }
  const select = node("select", "control");
  select.setAttribute("aria-label", name);
  select.dataset.fieldPath = JSON.stringify(path);
  const placeholder = node("option", "", current === OMITTED ? "Ommesso" : "Scegli formato");
  placeholder.value = "";
  placeholder.disabled = current === OMITTED;
  select.append(placeholder);
  for (const option of options) {
    const item = node("option", "", option === "string" ? "Percorso semplice" : "Percorso con sorgente/profondità");
    item.value = option;
    select.append(item);
  }
  select.value = selected;
  select.addEventListener("change", () => onSet(path, select.value === "string" ? "" : {}));
  field.append(select);
  if (current !== OMITTED) {
    const branch = schema.oneOf.find(option => (option.type ?? (option.properties ? "object" : "string")) === selected);
    if (selected === "object" && branch) renderFields(field, branch, current, path, onSet, onDelete);
    if (selected === "string" && branch) renderScalar(field, name, branch, current, path, onSet, onDelete);
  }
  container.append(field);
}

function makeReplacementButton(name, path, onSet, replacement) {
  const button = node("button", "w-fit rounded border px-2 py-1 text-xs hover:bg-slate-50", `Sostituisci ${name} con una struttura vuota`);
  button.type = "button";
  button.addEventListener("click", () => onSet(path, replacement));
  return button;
}

function validScalar(type, value) {
  if (type === "boolean") return typeof value === "boolean";
  if (type === "integer") return Number.isInteger(value);
  if (type === "number") return typeof value === "number" && Number.isFinite(value);
  if (type === "string") return typeof value === "string";
  return true;
}
