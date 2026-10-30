import * as YAML from "yaml";

export class PipelineDocument {
  constructor(document, source) {
    this.document = document;
    this.source = source;
    this.readOnlyReason = null;
    this.checkEditable();
  }

  static parse(source) {
    const document = YAML.parseDocument(source, { uniqueKeys: true, prettyErrors: true });
    if (document.errors.length) {
      throw new Error(document.errors.map(error => error.message).join("\n"));
    }
    return new PipelineDocument(document, source);
  }

  static empty() {
    const model = PipelineDocument.parse("variables: {}\npipeline: []\n");
    model.document.get("pipeline", true).flow = false;
    return model;
  }

  checkEditable() {
    if (this.document.warnings.length) {
      this.readOnlyReason = this.document.warnings.map(warning => warning.message).join("\n");
      return;
    }
    try {
      const value = this.document.toJS();
      if (!value || typeof value !== "object" || Array.isArray(value)) {
        this.readOnlyReason = "La radice YAML deve essere una mappa.";
        return;
      }
      if (value.pipeline !== undefined && !Array.isArray(value.pipeline)) {
        this.readOnlyReason = "Il campo pipeline deve essere una lista.";
        return;
      }
      if (value.variables !== undefined && (!value.variables || typeof value.variables !== "object" || Array.isArray(value.variables))) {
        this.readOnlyReason = "Il campo variables deve essere una mappa.";
      }
    } catch (error) {
      this.readOnlyReason = `La rappresentazione YAML non è modificabile in sicurezza: ${error.message}`;
    }
  }

  get editable() { return this.readOnlyReason === null; }
  get value() { return this.document.toJS() ?? {}; }
  get yaml() { return this.document.toString(); }
  get steps() { return this.value.pipeline ?? []; }
  get variables() { return this.value.variables ?? {}; }

  set(path, value) {
    this.ensureEditable();
    if (path[0] === "variables" && path.length > 1 && !this.document.has("variables")) {
      this.document.set("variables", this.document.createNode({}));
    }
    const existing = this.document.getIn(path, true);
    this.document.setIn(path, this.updatedNode(existing, value));
    if (path[0] === "variables") {
      const variables = this.document.get("variables", true);
      if (YAML.isMap(variables)) variables.flow = variables.items.length === 0;
    }
  }

  delete(path) {
    this.ensureEditable();
    this.document.deleteIn(path);
  }

  renameVariable(oldName, newName) {
    this.ensureEditable();
    const name = newName.trim();
    if (!name) throw new Error("Il nome della variabile non può essere vuoto.");
    const variables = this.document.get("variables", true);
    if (!YAML.isMap(variables)) throw new Error("Il campo variables non è una mappa modificabile.");
    if (name === oldName) return;
    if (variables.items.some(pair => variableKey(pair.key) === name)) {
      throw new Error(`Esiste già una variabile chiamata '${name}'.`);
    }

    const pair = variables.items.find(item => variableKey(item.key) === oldName);
    if (!pair) throw new Error(`Variabile '${oldName}' non trovata.`);
    const key = this.document.createNode(name);
    for (const property of ["commentBefore", "comment", "spaceBefore"]) {
      if (pair.key[property] !== undefined) key[property] = pair.key[property];
    }
    pair.key = key;
  }

  addStep(step) {
    this.ensureEditable();
    let sequence = this.document.get("pipeline", true);
    if (!YAML.isSeq(sequence)) {
      sequence = new YAML.YAMLSeq();
      this.document.set("pipeline", sequence);
    }
    sequence.add(this.document.createNode(step));
    return sequence.items.length - 1;
  }

  moveStep(index, destination) {
    this.ensureEditable();
    const sequence = this.document.get("pipeline");
    if (!YAML.isSeq(sequence) || destination < 0 || destination >= sequence.items.length) return;
    const [item] = sequence.items.splice(index, 1);
    sequence.items.splice(destination, 0, item);
  }

  removeStep(index) {
    this.ensureEditable();
    const sequence = this.document.get("pipeline");
    if (YAML.isSeq(sequence)) sequence.items.splice(index, 1);
  }

  duplicateStep(index, clone) {
    this.ensureEditable();
    const step = this.steps[index];
    if (!step) return -1;
    const duplicate = clone(step);
    duplicate.id = `${step.id ?? "step"}-copy`;
    return this.addStep(duplicate);
  }

  ensureEditable() {
    if (!this.editable) throw new Error(`Documento in sola lettura: ${this.readOnlyReason}`);
  }

  updatedNode(existing, value) {
    const updated = this.document.createNode(value);
    if (!existing || !updated || typeof updated !== "object") return updated;
    for (const property of ["commentBefore", "comment", "spaceBefore"]) {
      if (existing[property] !== undefined) updated[property] = existing[property];
    }

    if (YAML.isMap(existing) && YAML.isMap(updated)) {
      for (const pair of updated.items) {
        const key = variableKey(pair.key);
        const oldPair = existing.items.find(item => variableKey(item.key) === key);
        if (!oldPair) continue;
        pair.value = this.updatedNode(oldPair.value, pair.value?.toJSON?.() ?? pair.value);
        for (const property of ["commentBefore", "comment", "spaceBefore"]) {
          if (oldPair[property] !== undefined) pair[property] = oldPair[property];
        }
      }
    } else if (YAML.isSeq(existing) && YAML.isSeq(updated)) {
      const unused = new Set(existing.items);
      updated.items = updated.items.map((item, index) => {
        const oldAtIndex = existing.items[index];
        let oldItem = unused.has(oldAtIndex) && sameValue(oldAtIndex?.toJSON?.(), item?.toJSON?.()) ? oldAtIndex : null;
        if (!oldItem) oldItem = [...unused].find(candidate => sameValue(candidate?.toJSON?.(), item?.toJSON?.())) ?? null;
        if (!oldItem) return item;
        unused.delete(oldItem);
        return this.updatedNode(oldItem, item?.toJSON?.());
      });
    } else if (existing.constructor?.name === "Scalar" && updated.constructor?.name === "Scalar") {
      if (sameValue(existing.value, updated.value)) return existing;
    }
    return updated;
  }
}

export function newStep(type, index) {
  return {
    id: `${type.toLowerCase().replace(/[^a-z0-9]+/g, "-")}-${index + 1}`,
    type,
    enabled: true,
    config: {}
  };
}

function sameValue(left, right) {
  try {
    return JSON.stringify(left) === JSON.stringify(right);
  } catch {
    return Object.is(left, right);
  }
}

function variableKey(key) {
  if (typeof key === "string") return key;
  if (typeof key?.value === "string") return key.value;
  return String(key?.toJSON?.() ?? "");
}
