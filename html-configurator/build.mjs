import { execFileSync } from "node:child_process";
import { readFile, mkdir, writeFile, mkdtemp, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import os from "node:os";
import path from "node:path";

const root = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(root, "dist");
const schemaPath = process.argv[2] ?? path.join(dist, "actions.schema.json");
const schema = JSON.parse(await readFile(schemaPath, "utf8"));

await mkdir(dist, { recursive: true });
await writeFile(path.join(dist, "actions.schema.json"), `${JSON.stringify(schema, null, 2)}\n`);
const temporaryDirectory = await mkdtemp(path.join(os.tmpdir(), "terraform-configurator-"));
try {
  const cssPath = path.join(temporaryDirectory, "styles.css");
  const jsPath = path.join(temporaryDirectory, "app.js");
  execFileSync(process.execPath, [
    path.join(root, "node_modules", "tailwindcss", "lib", "cli.js"),
    "-c", path.join(root, "tailwind.config.js"),
    "-i", path.join(root, "src", "styles.css"),
    "-o", cssPath,
    "--minify"
  ], { stdio: "inherit" });
  execFileSync(process.execPath, [
    path.join(root, "node_modules", "esbuild", "bin", "esbuild"),
    path.join(root, "src", "app.js"),
    "--bundle", "--minify", "--format=iife",
    `--outfile=${jsPath}`
  ], { stdio: "inherit" });

  const template = await readFile(path.join(root, "src", "template.html"), "utf8");
  const css = await readFile(cssPath, "utf8");
  const js = await readFile(jsPath, "utf8");
  const embeddedSchema = JSON.stringify(schema).replaceAll("</", "<\\/");
  const html = template
    .replace("/*__TAILWIND_CSS__*/", css)
    .replace("/*__ACTION_SCHEMA__*/", embeddedSchema)
    .replace("/*__APP_JS__*/", js);
  await writeFile(path.join(dist, "terraform-configurator.html"), html);
} finally {
  await rm(temporaryDirectory, { recursive: true, force: true });
}
