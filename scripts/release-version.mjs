import { readFile, writeFile, appendFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";

const argument = process.argv[2];
if (argument === "--next") {
  const tags = execFileSync("git", ["tag", "-l", "--sort=-v:refname"], {
    encoding: "utf8",
  });
  const latest = tags
    .split(/\r?\n/)
    .find((tag) => /^v\d+\.\d+\.\d+$/.test(tag));
  const parts = latest ? latest.slice(1).split(".").map(Number) : [0, 1, -1];
  parts[2]++;
  const version = parts.join(".");
  if (!process.env.GITHUB_OUTPUT) throw new Error("GITHUB_OUTPUT is required");
  await appendFile(
    process.env.GITHUB_OUTPUT,
    `version=${version}\ntag=v${version}\n`,
  );
} else {
  if (!/^\d+\.\d+\.\d+$/.test(argument ?? ""))
    throw new Error("Expected a semantic version");
  for (const file of [
    "package.json",
    "package-lock.json",
    "src-tauri/tauri.conf.json",
  ]) {
    const data = JSON.parse(await readFile(file, "utf8"));
    data.version = argument;
    if (data.packages?.[""]) data.packages[""].version = argument;
    await writeFile(file, `${JSON.stringify(data, null, 2)}\n`);
  }
  const cargo = await readFile("src-tauri/Cargo.toml", "utf8");
  await writeFile(
    "src-tauri/Cargo.toml",
    cargo.replace(/^version = "[^"]+"/m, `version = "${argument}"`),
  );
}
