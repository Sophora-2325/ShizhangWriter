import { useEffect, useMemo, useState } from "react";
import { api, Chapter, Project } from "./api";
import { CreateProjectModal } from "./CreateProjectModal";
import { ChapterList } from "./ChapterList";
import { CenterPane } from "./CenterPane";
import { RightPane } from "./RightPane";

export default function App() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [project, setProject] = useState<Project | null>(null);
  const [chapters, setChapters] = useState<Chapter[]>([]);
  const [selectedNo, setSelectedNo] = useState<number | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [showCreate, setShowCreate] = useState(false);
  const [costTotal, setCostTotal] = useState(0);
  const [apiKeyDraft, setApiKeyDraft] = useState("");
  const [hasApiKey, setHasApiKey] = useState(false);
  const [keyHint, setKeyHint] = useState("");
  const [keyMsg, setKeyMsg] = useState("");

  const selected = useMemo(
    () => chapters.find((c) => c.no === selectedNo) || null,
    [chapters, selectedNo]
  );

  async function refreshSettings() {
    const s = (await api.settings()) as {
      has_deepseek_key?: boolean;
      key_hint?: string;
    };
    setHasApiKey(Boolean(s.has_deepseek_key));
    setKeyHint(s.key_hint || "");
  }

  async function refreshProjects() {
    const list = await api.listProjects();
    setProjects(list);
  }

  async function loadProject(id: string) {
    setError("");
    const p = await api.getProject(id);
    const chs = await api.chapters(id);
    setProject(p);
    setChapters(chs);
    setSelectedNo(chs.length ? chs[chs.length - 1].no : null);
    const cost = await api.cost(id);
    setCostTotal(cost.total_cost_usd);
  }

  useEffect(() => {
    Promise.all([refreshProjects(), refreshSettings()]).catch((e) =>
      setError(String(e.message || e))
    );
  }, []);

  async function saveApiKey(clear = false) {
    const value = clear ? "" : apiKeyDraft.trim();
    if (!clear && !value) {
      setKeyMsg("请先输入密钥，或点「清除」删除已存密钥");
      return;
    }
    await api.updateSettings({ deepseek_api_key: value });
    if (value) {
      try {
        localStorage.setItem("novel-gm.has_deepseek_key", "1");
        localStorage.setItem("novel-gm.key_hint", "…" + value.slice(-4));
      } catch {
        /* ignore */
      }
      setApiKeyDraft("");
      setKeyMsg("密钥已保存到本地，下次无需再输入");
    } else {
      try {
        localStorage.removeItem("novel-gm.has_deepseek_key");
        localStorage.removeItem("novel-gm.key_hint");
      } catch {
        /* ignore */
      }
      setApiKeyDraft("");
      setKeyMsg("已清除本地密钥");
    }
    await refreshSettings();
  }

  async function withBusy<T>(fn: () => Promise<T>): Promise<T | undefined> {
    setBusy(true);
    setError("");
    try {
      return await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      return undefined;
    } finally {
      setBusy(false);
    }
  }

  async function reload() {
    if (!project) return;
    await loadProject(project.id);
  }

  return (
    <div className="app">
      <div className="topbar">
        <span className="title">novel-gm</span>
        <select
          value={project?.id || ""}
          onChange={(e) => {
            if (e.target.value) loadProject(e.target.value);
          }}
        >
          <option value="">选择项目…</option>
          {projects.map((p) => (
            <option key={p.id} value={p.id}>
              {p.title}
            </option>
          ))}
        </select>
        <button type="button" onClick={() => setShowCreate(true)}>
          新建
        </button>
        {project && (
          <>
            <span className="phase">{project.phase}</span>
            <span className="muted">ch{project.current_chapter}</span>
            <select
              value={project.provider}
              disabled={project.offline_mode}
              onChange={(e) =>
                withBusy(async () => {
                  await api.patchProjectSettings(project.id, {
                    provider: e.target.value,
                  });
                  await reload();
                })
              }
            >
              <option value="deepseek">DeepSeek</option>
              <option value="local">本地</option>
              <option value="mock">Mock</option>
            </select>
            <label className="muted">
              <input
                type="checkbox"
                checked={project.offline_mode}
                onChange={(e) =>
                  withBusy(async () => {
                    await api.patchProjectSettings(project.id, {
                      offline_mode: e.target.checked,
                    });
                    await reload();
                  })
                }
              />{" "}
              离线
            </label>
          </>
        )}
        <span className="spacer" />
        <span className="cost">≈ ${costTotal.toFixed(4)}</span>
        {hasApiKey ? (
          <span className="ok" title="密钥只存于本机 Windows 凭据管理器">
            Key 已保存{keyHint ? ` ${keyHint}` : ""}
          </span>
        ) : (
          <span className="muted">未保存 Key</span>
        )}
        <input
          style={{ width: 160 }}
          type="password"
          placeholder={hasApiKey ? "输入新密钥以覆盖" : "DeepSeek API Key"}
          value={apiKeyDraft}
          onChange={(e) => {
            setApiKeyDraft(e.target.value);
            setKeyMsg("");
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              withBusy(() => saveApiKey(false));
            }
          }}
        />
        <button type="button" onClick={() => withBusy(() => saveApiKey(false))}>
          保存密钥
        </button>
        {hasApiKey && (
          <button type="button" onClick={() => withBusy(() => saveApiKey(true))}>
            清除
          </button>
        )}
        {keyMsg && <span className="muted">{keyMsg}</span>}
        {project && (
          <>
            <button
              type="button"
              onClick={() =>
                withBusy(async () => {
                  const r = await api.export(project.id, "markdown");
                  alert(`已导出: ${r.path}`);
                })
              }
            >
              MD
            </button>
            <button
              type="button"
              onClick={() =>
                withBusy(async () => {
                  const r = await api.export(project.id, "epub");
                  alert(`已导出: ${r.path}`);
                })
              }
            >
              EPUB
            </button>
          </>
        )}
      </div>
      {error && (
        <div className="toolbar error" style={{ borderBottom: "1px solid var(--border)" }}>
          {error}
        </div>
      )}
      {!project ? (
        <div className="empty">新建或选择一个项目开始。无 API Key 时自动使用 Mock Provider。</div>
      ) : (
        <div className="workspace">
          <ChapterList
            chapters={chapters}
            selectedNo={selectedNo}
            onSelect={setSelectedNo}
            busy={busy}
            onPlan={() =>
              withBusy(async () => {
                const ch = await api.plan(project.id);
                await reload();
                if (ch) setSelectedNo(ch.no);
              })
            }
            onRollback={() => {
              if (selectedNo == null) return;
              if (!confirm(`回滚到第 ${selectedNo} 章并作废其后？`)) return;
              withBusy(async () => {
                await api.rollback(project.id, selectedNo);
                await reload();
              });
            }}
          />
          <CenterPane
            project={project}
            chapter={selected}
            busy={busy}
            onRefresh={reload}
            withBusy={withBusy}
          />
          <RightPane project={project} onRefresh={reload} withBusy={withBusy} />
        </div>
      )}
      {showCreate && (
        <CreateProjectModal
          onClose={() => setShowCreate(false)}
          onCreated={async (p) => {
            setShowCreate(false);
            await refreshProjects();
            await loadProject(p.id);
          }}
        />
      )}
    </div>
  );
}
