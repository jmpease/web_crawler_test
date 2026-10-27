// Installs, in a running debug-stack container, the vsdbg build that the local VS Code C# extension expects (D-31).
// An older vsdbg (getvsdbg.sh's "latest" lags the extension) rejects the extension's breakpoint requests, so
// breakpoints silently never bind. Run by the "vsdbg: sync <service>" tasks before each Docker attach. It first
// restarts the service if an earlier debug session left it suspended (D-32).
//
// Usage: node .vscode/sync-vsdbg.mjs <compose service>
import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const service = process.argv[2];
if (!service) fail("Usage: node .vscode/sync-vsdbg.mjs <compose service>");

restartIfLeftSuspended(service);
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

/**
 * Stopping a Docker debug session can leave the app suspended: vsdbg suspends the runtime to detach, VS Code ends
 * `docker compose exec` before it finishes, and nothing resumes the app (D-32). A vsdbg still running from that
 * session also blocks the next attach (0x80131c08). Killing vsdbg doesn't resume the app, so restart the service.
 * vsdbg renames its process, so pgrep matches the command line; the ^ keeps it from matching this sh -c script.
 * Any HTTP answer counts as alive (a 503 just means the database or broker is down); only a timeout means suspended.
 */
function restartIfLeftSuspended(service) {
  const check = `
    if pgrep -f '^/vsdbg/vsdbg ' >/dev/null; then echo "a vsdbg from an earlier debug session is still running"; exit 0; fi
    code=0; curl -s -o /dev/null -m 3 http://localhost:8080/health || code=$?
    if [ "$code" = 28 ]; then echo "the app doesn't answer /health, so an earlier debug session left it suspended"; else echo ok; fi`;
  const state = docker(["compose", "exec", "-T", service, "sh", "-c", check]).trim();
  if (state === "ok") return;

  console.log(`${service}: ${state}. Restarting ${service}.`);
  docker(["compose", "restart", service]);
  const wait = "for i in $(seq 60); do curl -s -o /dev/null -m 2 http://localhost:8080/health && exit 0; sleep 1; done; exit 1";
  docker(["compose", "exec", "-T", service, "sh", "-c", wait], `${service} didn't answer /health within 60 s of restarting.`);
  console.log(`${service} restarted.`);
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

function docker(args, failure = "Is the debug stack running?") {
  const result = spawnSync("docker", args, { encoding: "utf8" });
  if (result.error) fail(`Could not run docker: ${result.error.message}`);
  if (result.status !== 0) {
    fail(`docker ${args.slice(0, 4).join(" ")} … failed (exit ${result.status}). ${failure}\n${result.stderr}`);
  }
  return result.stdout;
}

function fail(message) {
  console.error(message);
  process.exit(1);
}
