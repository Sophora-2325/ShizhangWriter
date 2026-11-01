/**
 * Start FastAPI from novel-gm root using .venv or py -3.13
 */
const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const venvPy = path.join(root, ".venv", "Scripts", "python.exe");
const py = fs.existsSync(venvPy) ? venvPy : "py";
const args = fs.existsSync(venvPy)
  ? ["-m", "services.core"]
  : ["-3.13", "-m", "services.core"];

const child = spawn(py, args, {
  cwd: root,
  env: {
    ...process.env,
    NOVEL_GM_ROOT: root,
    NOVEL_GM_DATA: process.env.NOVEL_GM_DATA || path.join(root, "storage"),
  },
  stdio: "inherit",
  shell: !fs.existsSync(venvPy),
});

child.on("exit", (code) => process.exit(code ?? 0));
