/** Kill listeners on novel-gm API/Web ports (Windows-friendly). */
const { execSync } = require("child_process");

const ports = [18765, 5173];

function killPort(port) {
  try {
    const out = execSync(`netstat -ano | findstr :${port}`, { encoding: "utf8" });
    const pids = new Set();
    for (const line of out.split(/\r?\n/)) {
      if (!line.includes("LISTENING")) continue;
      const parts = line.trim().split(/\s+/);
      const pid = parts[parts.length - 1];
      if (pid && /^\d+$/.test(pid) && pid !== "0") pids.add(pid);
    }
    for (const pid of pids) {
      try {
        execSync(`taskkill /PID ${pid} /F`, { stdio: "ignore" });
        console.log(`killed PID ${pid} (port ${port})`);
      } catch {
        /* ignore */
      }
    }
  } catch {
    console.log(`no listener on ${port}`);
  }
}

for (const p of ports) killPort(p);
console.log("done");
