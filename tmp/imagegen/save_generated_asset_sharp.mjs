import fsSync from "node:fs";
import fs from "node:fs/promises";
import path from "node:path";
import sharp from "sharp";

const ROOT = "C:/Users/alpac/Desktop/BP0108";
const GENERATED_ROOT = "C:/Users/alpac/.codex/generated_images/019f3825-d9ea-73a3-9bf1-077a88e95e02";
const IMAGE_DIR = path.join(ROOT, "question_images");
const TMP = path.join(ROOT, "tmp", "imagegen");
const PROGRESS_PATH = path.join(TMP, "replacement_progress.json");
const MANIFEST_PATH = path.join(ROOT, "stream_questions_100.json");

function suffixNum(qid) {
  const match = String(qid).match(/_(\d+)$/);
  return match ? Number(match[1]) : 0;
}

function sortKey(qid) {
  const group = qid.startsWith("team_more_") ? 0 : qid.startsWith("nonteam_more_") ? 1 : 2;
  return [group, suffixNum(qid), qid];
}

function compareEntries(a, b) {
  const ka = sortKey(a.id || "");
  const kb = sortKey(b.id || "");
  for (let i = 0; i < ka.length; i += 1) {
    if (ka[i] < kb[i]) return -1;
    if (ka[i] > kb[i]) return 1;
  }
  return 0;
}

function localIso() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function winPath(p) {
  return path.win32.normalize(p.replaceAll("/", "\\"));
}

function normPath(p) {
  return path.win32.normalize(p.replaceAll("/", "\\")).toLowerCase();
}

async function loadPromptRows() {
  const rows = {};
  for (const name of ["team_more_prompts.jsonl", "nonteam_more_prompts.jsonl"]) {
    const promptPath = path.join(TMP, name);
    if (!fsSync.existsSync(promptPath)) continue;
    const lines = (await fs.readFile(promptPath, "utf8")).split(/\r?\n/);
    for (const line of lines) {
      if (!line.trim()) continue;
      const row = JSON.parse(line);
      rows[row.id] = row;
    }
  }
  return rows;
}

async function latestUnusedSource(progress) {
  const used = new Set(progress.filter((entry) => entry.source).map((entry) => normPath(entry.source)));
  const names = (await fs.readdir(GENERATED_ROOT)).filter((name) => name.endsWith(".png"));
  const candidates = [];
  for (const name of names) {
    const source = path.join(GENERATED_ROOT, name);
    const stat = await fs.stat(source);
    candidates.push({ source, mtime: stat.mtimeMs });
  }
  candidates.sort((a, b) => b.mtime - a.mtime);
  for (const candidate of candidates) {
    if (!used.has(normPath(candidate.source))) return candidate.source;
  }
  throw new Error(`No unused generated PNG found in ${GENERATED_ROOT}`);
}

export async function saveGenerated(qid, sourceOverride) {
  const manifest = JSON.parse(await fs.readFile(MANIFEST_PATH, "utf8"));
  const items = Object.fromEntries(manifest.items.map((item) => [item.id, item]));
  if (!items[qid]) throw new Error(`${qid} is not in ${MANIFEST_PATH}`);

  let progress = JSON.parse(await fs.readFile(PROGRESS_PATH, "utf8"));
  const source = sourceOverride || (await latestUnusedSource(progress));
  const destination = path.join(IMAGE_DIR, `${qid}.png`);

  await sharp(source).resize(2048, 1152, { fit: "cover", position: "center" }).png().toFile(destination);

  const promptRows = await loadPromptRows();
  const item = items[qid];
  const row = promptRows[qid] || {};

  progress = progress.filter((entry) => entry.id !== qid);
  progress.push({
    id: qid,
    answer: item.answer || row.answer || "",
    source: winPath(source),
    destination: winPath(destination),
    mode: "built-in image_gen",
    normalized_size: [2048, 1152],
    replaced_at: localIso(),
    prompt: row.prompt || item.meta?.image_prompt || "",
  });
  progress.sort(compareEntries);
  await fs.writeFile(PROGRESS_PATH, `${JSON.stringify(progress, null, 2)}\n`, "utf8");

  const metadata = await sharp(destination).metadata();
  return {
    id: qid,
    source: winPath(source),
    destination: winPath(destination),
    width: metadata.width,
    height: metadata.height,
    progress_entries: progress.length,
  };
}
