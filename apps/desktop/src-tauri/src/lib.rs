use std::fs::{self, OpenOptions};
use std::io::Write;
use std::net::TcpStream;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::thread;
use std::time::Duration;
use tauri::{Manager, RunEvent};

const API_HOST: &str = "127.0.0.1";
const API_PORT: u16 = 18765;

struct Backend(Mutex<Option<Child>>);

fn api_base() -> String {
    format!("http://{API_HOST}:{API_PORT}")
}

fn port_open() -> bool {
    TcpStream::connect((API_HOST, API_PORT)).is_ok()
}

fn health_ok() -> bool {
    let url = format!("{}/health", api_base());
    // tiny blocking check via tcp + HTTP GET with std only is hard;
    // treat open port as enough; finer check after spawn wait loop uses TCP.
    port_open()
}

fn find_novel_root(app: &tauri::AppHandle) -> Option<PathBuf> {
    if let Ok(p) = std::env::var("NOVEL_GM_ROOT") {
        let pb = PathBuf::from(p);
        if pb.join("services").join("core").exists() {
            return Some(pb);
        }
    }

    // Dev: walk up from cwd looking for services/core
    if let Ok(cwd) = std::env::current_dir() {
        for anc in cwd.ancestors().take(8) {
            if anc.join("services").join("core").join("main.py").exists() {
                return Some(anc.to_path_buf());
            }
        }
    }

    // Resource / exe adjacent (packaged layout: resources/novel-gm or sibling)
    if let Ok(res) = app.path().resource_dir() {
        let candidates = [
            res.clone(),
            res.join("novel-gm"),
            res.join("..").join("novel-gm"),
        ];
        for c in candidates {
            if c.join("services").join("core").join("main.py").exists() {
                return Some(c.canonicalize().unwrap_or(c));
            }
        }
    }

    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            for anc in dir.ancestors().take(6) {
                if anc.join("services").join("core").join("main.py").exists() {
                    return Some(anc.to_path_buf());
                }
            }
        }
    }
    None
}

fn data_dir(app: &tauri::AppHandle) -> PathBuf {
    if let Ok(p) = std::env::var("NOVEL_GM_DATA") {
        return PathBuf::from(p);
    }
    app.path()
        .app_data_dir()
        .unwrap_or_else(|_| PathBuf::from(".").join("novel-gm-data"))
        .join("storage")
}

fn log_line(log_path: &PathBuf, msg: &str) {
    let _ = fs::create_dir_all(log_path.parent().unwrap_or(log_path.as_path()));
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(log_path) {
        let _ = writeln!(f, "{msg}");
    }
}

fn spawn_backend(app: &tauri::AppHandle) -> Option<Child> {
    let log_path = data_dir(app).join("backend.log");

    if health_ok() {
        log_line(&log_path, "backend already listening; skip spawn");
        return None;
    }

    let root = match find_novel_root(app) {
        Some(r) => r,
        None => {
            log_line(&log_path, "ERROR: cannot find NOVEL_GM_ROOT (services/core)");
            return None;
        }
    };

    let data = data_dir(app);
    let _ = fs::create_dir_all(&data);
    log_line(
        &log_path,
        &format!("spawning backend root={} data={}", root.display(), data.display()),
    );

    let python = root.join(".venv/Scripts/python.exe");
    let mut cmd = if python.exists() {
        Command::new(&python)
    } else {
        let mut c = Command::new("py");
        c.arg("-3.13");
        c
    };

    let log_file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(&log_path)
        .ok();

    cmd.env("NOVEL_GM_ROOT", &root)
        .env("NOVEL_GM_DATA", &data)
        .env("NOVEL_GM_USE_APPDATA", "0")
        .arg("-m")
        .arg("services.core")
        .current_dir(&root);

    if let Some(f) = log_file {
        let f2 = f.try_clone().ok();
        cmd.stdout(Stdio::from(f));
        if let Some(f2) = f2 {
            cmd.stderr(Stdio::from(f2));
        } else {
            cmd.stderr(Stdio::null());
        }
    } else {
        cmd.stdout(Stdio::null()).stderr(Stdio::null());
    }

    match cmd.spawn() {
        Ok(child) => {
            // wait up to ~15s for port
            for i in 0..30 {
                if port_open() {
                    log_line(&log_path, &format!("backend ready after {} checks", i + 1));
                    return Some(child);
                }
                thread::sleep(Duration::from_millis(500));
            }
            log_line(&log_path, "WARN: backend spawned but port not open in time");
            Some(child)
        }
        Err(e) => {
            log_line(&log_path, &format!("ERROR spawn failed: {e}"));
            None
        }
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .setup(|app| {
            let child = spawn_backend(app.handle());
            app.manage(Backend(Mutex::new(child)));
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|app_handle, event| {
            if let RunEvent::Exit = event {
                if let Some(state) = app_handle.try_state::<Backend>() {
                    if let Ok(mut guard) = state.0.lock() {
                        if let Some(mut child) = guard.take() {
                            let _ = child.kill();
                        }
                    }
                }
            }
        });
}
