// Installs, in a running debug-stack container, the vsdbg build that the local VS Code C# extension expects (D-31).
// An older vsdbg (getvsdbg.sh's "latest" lags the extension) rejects the extension's breakpoint requests, so
// breakpoints silently never bind. Run by the "vsdbg: sync <service>" tasks before each Docker attach.
//
// Usage: node .vscode/sync-vsdbg.mjs <compose service>
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const service = process.argv[2];
if (!service) fail("Usage: node .vscode/sync-vsdbg.mjs <compose service>");

const extension = findCSharpExtension();
const arch = containerArch(service);
const debuggerPackage = findLinuxDebugger(extension, arch);
installInContainer(service, debuggerPackage);

/** The newest installed ms-dotnettools.csharp extension; older versions can linger until VS Code cleans them up. */
function findCSharpExtension() {
  const roots = [
    process.env.VSCODE_EXTENSIONS,
    join(homedir(), ".vscode", "extensions"),
    join(homedir(), ".vscode-insiders", "extensions"),
    join(homedir(), ".vscode-server", "extensions"),
  ].filter((root) => root && existsSync(root));

  const candidates = roots.flatMap((root) =>
    readdirSync(root)
      .map((name) => ({ name, match: /^ms-dotnettools\.csharp-(\d+)\.(\d+)\.(\d+)/.exec(name) }))
      .filter(({ match }) => match)
      .map(({ name, match }) => ({ dir: join(root, name), version: match.slice(1).map(Number) })));
  if (candidates.length === 0) fail(`C# extension (ms-dotnettools.csharp) not found under: ${roots.join(", ")}`);

  candidates.sort((a, b) => compareVersions(b.version, a.version));
  return candidates[0];
}

function compareVersions(a, b) {
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

/** The C# extension names architectures x86_64 and arm64. */
function containerArch(service) {
  const machine = docker(["compose", "exec", "-T", service, "uname", "-m"]).trim();
  if (machine === "x86_64") return "x86_64";
  if (machine === "aarch64") return "arm64";
  fail(`Unsupported container architecture '${machine}'.`);
}

/** The extension's own download entry for the Linux (glibc) debugger: URL plus SHA-256. */
function findLinuxDebugger(extension, arch) {
  const manifest = JSON.parse(readFileSync(join(extension.dir, "package.json"), "utf8"));
  const entry = (manifest.runtimeDependencies ?? []).find((dependency) =>
    dependency.id === "Debugger"
    && dependency.platforms?.includes("linux")
    && dependency.architectures?.includes(arch));
  if (!entry?.url || !entry.integrity) fail(`No linux/${arch} debugger listed in ${extension.dir}/package.json.`);
  console.log(`C# extension ${extension.version.join(".")}: vsdbg for linux/${arch}`);
  return { url: entry.url, sha256: entry.integrity.toLowerCase() };
}

/** Root is needed to write /vsdbg; the download is skipped when the container already has this build. */
function installInContainer(service, { url, sha256 }) {
  const script = `
    set -e
    if [ -x /vsdbg/vsdbg ] && [ "$(cat /vsdbg/.source-url 2>/dev/null)" = "$VSDBG_URL" ]; then
      echo "vsdbg up to date: $(head -n 1 /vsdbg/version.txt)"; exit 0
    fi
    echo "Installing vsdbg from $VSDBG_URL"
    curl -fsSL -o /tmp/vsdbg.zip "$VSDBG_URL"
    echo "$VSDBG_SHA256  /tmp/vsdbg.zip" | sha256sum -c --status - || { echo "vsdbg download failed its SHA-256 check" >&2; exit 1; }
    rm -rf /vsdbg && mkdir /vsdbg && unzip -q /tmp/vsdbg.zip -d /vsdbg && rm /tmp/vsdbg.zip
    chmod +x /vsdbg/vsdbg
    echo "$VSDBG_URL" > /vsdbg/.source-url
    echo "Installed vsdbg $(head -n 1 /vsdbg/version.txt)"`;
  process.stdout.write(docker([
    "compose", "exec", "-T", "-u", "0", "-e", `VSDBG_URL=${url}`, "-e", `VSDBG_SHA256=${sha256}`,
    service, "sh", "-c", script,
  ]));
}

function docker(args) {
  const result = spawnSync("docker", args, { encoding: "utf8" });
  if (result.error) fail(`Could not run docker: ${result.error.message}`);
  if (result.status !== 0) {
    fail(`docker ${args.slice(0, 4).join(" ")} … failed (exit ${result.status}). Is the debug stack running?\n${result.stderr}`);
  }
  return result.stdout;
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
