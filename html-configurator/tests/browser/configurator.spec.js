import { test, expect } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { fileURLToPath, pathToFileURL } from "node:url";
import { tmpdir } from "node:os";
import path from "node:path";

const html = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../dist/terraform-configurator.html");
const localUrl = pathToFileURL(html).href;

test("standalone configurator loads offline, edits nested fields, reorders, and downloads YAML", async ({ page }) => {
  const externalRequests = [];
  page.on("request", request => {
    if (!request.url().startsWith("file:")) externalRequests.push(request.url());
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(localUrl);
  await expect(page.getByRole("heading", { name: "Configuratore pipeline" })).toBeVisible();
  await page.getByRole("button", { name: "Aggiungi variabile" }).click();
  await page.getByRole("textbox", { name: "Nome variabile" }).fill("SourceServer");
  await page.getByRole("textbox", { name: "Nome variabile" }).press("Tab");
  await page.getByRole("textbox", { name: "Valore SourceServer" }).fill("TEBTCH03");
  await expect(page.getByText("Modifiche non caricate")).toBeVisible();
  await expect(page.locator("#yaml-preview")).not.toContainText("TEBTCH03");
  await page.getByRole("button", { name: "Aggiorna anteprima" }).click();
  await expect(page.locator("#yaml-preview")).toContainText("variables:\n  SourceServer: TEBTCH03");
  await expect(page.locator("#yaml-preview")).not.toContainText("variables: {");
  await page.getByRole("button", { name: "Aggiungi", exact: true }).click();
  await page.getByRole("button", { name: "Aggiungi elemento a paths" }).click();
  const variant = page.getByRole("combobox", { name: "paths[0]" });
  await variant.selectOption("string");
  const pathInput = page.getByRole("textbox", { name: "paths[0]", exact: true });
  await pathInput.fill("D:\\first-path");
  await expect(pathInput).toBeFocused();
  await expect(page.getByText("Modifiche non caricate")).toBeVisible();
  await page.getByRole("button", { name: "Aggiorna anteprima" }).click();
  await expect(page.locator("#yaml-preview")).toContainText("D:\\first-path");
  await variant.selectOption("object");
  await page.getByRole("textbox", { name: "path" }).fill("D:\\data");
  await page.getByRole("spinbutton", { name: "depth" }).fill("1");
  await page.getByRole("button", { name: "Aggiorna anteprima" }).click();
  await expect(page.getByText("source · richiesto")).toBeVisible();
  await page.getByRole("textbox", { name: "source", exact: true }).fill("\\\\server\\share");
  await page.getByRole("button", { name: "Aggiungi", exact: true }).click();
  await page.getByRole("button", { name: "↑ Sposta", exact: true }).click();
  await expect(page.locator("#step-list li").first()).toContainText("required-folders-2");
  await page.getByRole("button", { name: "Elimina" }).click();
  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("button", { name: "Scarica YAML" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe("pipeline.yaml");
  const exported = await readFile(await download.path(), "utf8");
  expect(exported).toContain("source: \\\\server\\share");
  expect(exported).toMatch(/variables:\r?\n  SourceServer: TEBTCH03/);
  const cli = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../bin/Debug/net9.0/terraform.dll");
  const validationDirectory = await mkdtemp(path.join(tmpdir(), "terraform-configurator-validation-"));
  try {
    const yamlPath = path.join(validationDirectory, "pipeline.yaml");
    await writeFile(yamlPath, exported);
    const validation = execFileSync("dotnet", [cli, "validate", yamlPath], { encoding: "utf8" });
    expect(validation).toContain("Configurazione valida");
  } finally {
    await rm(validationDirectory, { recursive: true, force: true });
  }
  expect(externalRequests).toEqual([]);
});

test("compact layout remains usable on a mobile viewport", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(localUrl);
  await expect(page.getByRole("button", { name: "Aggiungi", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Aggiungi", exact: true }).click();
  await expect(page.getByRole("combobox", { name: "Action" })).toBeVisible();
});
