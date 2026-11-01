import { useState } from "react";
import DiffMatchPatch from "diff-match-patch";
import { api, Character, Project } from "./api";
import { CharacterProfileModal } from "./CharacterProfileModal";

/** 与后端 models.LORE_CATEGORIES 顺序一致。 */
const CATEGORIES: [string, string][] = [
  ["world", "世界观"],
  ["geo", "地理"],
  ["faction", "势力"],
  ["rule", "规则"],
  ["history", "历史"],
  ["other", "其他"],
];

export function RightPane({
  project,
  onRefresh,
  withBusy,
}: {
  project: Project;
  onRefresh: () => Promise<void>;
  withBusy: <T>(fn: () => Promise<T>) => Promise<T | undefined>;
}) {
  const [editPath, setEditPath] = useState("entries.0.content");
  const [editOld, setEditOld] = useState("");
  const [editNew, setEditNew] = useState("");
  const [diffText, setDiffText] = useState("");
  const [tab, setTab] = useState<"chars" | "threads" | "style" | "rules">("chars");
  const [listOpen, setListOpen] = useState(true);
  const [editing, setEditing] = useState<Character | null | undefined>(undefined);
  // undefined = closed; null = new character; Character = edit existing

  function previewDiff(oldV: string, newV: string) {
    const dmp = new DiffMatchPatch();
    const diffs = dmp.diff_main(oldV || "", newV || "");
    dmp.diff_cleanupSemantic(diffs);
    const html = diffs
      .map(([op, text]) => {
        if (op === 1) return `【+${text}】`;
        if (op === -1) return `【-${text}】`;
        return text;
      })
      .join("");
    setDiffText(html);
  }

  function profileComplete(c: Character): boolean {
    return Boolean(
      c.name?.trim() &&
        c.age?.trim() &&
        c.personality?.trim() &&
        c.appearance?.trim() &&
        c.outfit?.trim() &&
        c.recent_thoughts?.trim()
    );
  }

  return (
    <div className="pane">
      <div className="pane-header">设定</div>
      <div className="toolbar">
        <button type="button" onClick={() => setTab("chars")}>
          人物
        </button>
        <button type="button" onClick={() => setTab("threads")}>
          脉络
        </button>
        <button type="button" onClick={() => setTab("style")}>
          文风
        </button>
        <button type="button" onClick={() => setTab("rules")}>
          规则
        </button>
      </div>
      <div className="pane-body">
        <div className="section">
          <h3>背景设定</h3>
          {project.entries.length === 0 && <div>（无）</div>}
          {CATEGORIES.map(([key, label]) => {
            const group = project.entries.filter((e) => e.category === key);
            if (group.length === 0) return null;
            return (
              <div key={key}>
                <h4>{label}</h4>
                {group.map((e) => (
                  <div key={e.id} className="char-card">
                    {key === "rule" && e.hard ? "[硬] " : ""}
                    {e.title ? `${e.title}：` : ""}
                    {e.content}
                  </div>
                ))}
              </div>
            );
          })}
        </div>

        {tab === "chars" && (
          <div className="section">
            <div className="toolbar" style={{ border: "none", padding: "0 0 8px" }}>
              <button
                type="button"
                className="primary"
                onClick={() => {
                  setListOpen(true);
                }}
              >
                添加人物
              </button>
              <button type="button" onClick={() => setListOpen((v) => !v)}>
                {listOpen ? "收起列表" : "展开列表"}
              </button>
            </div>

            {listOpen && (
              <div className="char-list">
                <div className="toolbar" style={{ border: "none", padding: "0 0 6px" }}>
                  <button type="button" className="primary" onClick={() => setEditing(null)}>
                    ＋ 添加
                  </button>
                </div>
                {project.characters.length === 0 && (
                  <div className="muted">尚无人物，点击「添加」创建</div>
                )}
                {project.characters.map((c) => (
                  <div
                    key={c.id}
                    className="char-card char-list-item"
                    onClick={() => setEditing(c)}
                    role="button"
                    tabIndex={0}
                    onKeyDown={(e) => {
                      if (e.key === "Enter") setEditing(c);
                    }}
                  >
                    <div className="name">
                      {c.name || "未命名"}
                      {!profileComplete(c) && (
                        <span className="error" style={{ marginLeft: 6, fontWeight: 400 }}>
                          资料未齐
                        </span>
                      )}
                    </div>
                    <div className="muted">
                      {[c.age && `${c.age}岁`, c.role, c.personality?.slice(0, 24)]
                        .filter(Boolean)
                        .join(" · ") || "点击编辑个人资料"}
                    </div>
                    <div className="char-list-actions">
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          setEditing(c);
                        }}
                      >
                        资料
                      </button>
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          if (!confirm(`删除人物「${c.name}」？`)) return;
                          withBusy(async () => {
                            await api.deleteCharacter(project.id, c.id);
                            await onRefresh();
                          });
                        }}
                      >
                        删除
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}

        {tab === "threads" && (
          <div className="section">
            <h3>脉络</h3>
            {project.threads.map((t) => (
              <div key={t.id} className="char-card">
                <div className="name">
                  {t.name} · {t.state}
                </div>
                <div>{t.user_directive}</div>
                <div className="muted">下一里程碑：{t.next_milestone || "—"}</div>
              </div>
            ))}
          </div>
        )}

        {tab === "style" && (
          <div className="section">
            <h3>文风</h3>
            <div>
              {project.style.pov} / {project.style.tense}
            </div>
            <div className="muted">
              禁用词 {project.style.banned_words?.length || 0} 条
            </div>
            <pre style={{ whiteSpace: "pre-wrap", fontSize: 11 }}>
              {project.style.compiled_system_constraints}
            </pre>
          </div>
        )}

        {tab === "rules" && (
          <div className="section">
            <h3>规则</h3>
            {project.entries.filter((e) => e.category === "rule").length === 0 && <div>（无）</div>}
            {project.entries
              .filter((e) => e.category === "rule")
              .map((e) => (
                <div key={e.id} className="char-card">
                  {e.hard ? "[硬] " : ""}
                  {e.title ? `${e.title}：` : ""}
                  {e.content}
                </div>
              ))}
            <h3>禁忌</h3>
            <div>{project.forbidden.join("、") || "（无）"}</div>
          </div>
        )}

        {tab !== "chars" && (
          <div className="section">
            <h3>Bible 修改（diff 预览）</h3>
            <div className="field">
              <label>path（如 entries.0.content）</label>
              <input value={editPath} onChange={(e) => setEditPath(e.target.value)} />
            </div>
            <div className="field">
              <label>old</label>
              <textarea rows={2} value={editOld} onChange={(e) => setEditOld(e.target.value)} />
            </div>
            <div className="field">
              <label>new</label>
              <textarea
                rows={2}
                value={editNew}
                onChange={(e) => {
                  setEditNew(e.target.value);
                  previewDiff(editOld, e.target.value);
                }}
              />
            </div>
            {diffText && <div className="diff-pre">{diffText}</div>}
            <div className="toolbar" style={{ border: "none", padding: 0 }}>
              <button
                type="button"
                onClick={() => {
                  previewDiff(editOld, editNew);
                  withBusy(async () => {
                    await api.bible(
                      project.id,
                      [{ path: editPath, old: editOld, new: editNew, reason: "manual" }],
                      true
                    );
                  });
                }}
              >
                预览
              </button>
              <button
                type="button"
                className="primary"
                onClick={() =>
                  withBusy(async () => {
                    const r = await api.bible(
                      project.id,
                      [{ path: editPath, old: editOld, new: editNew, reason: "manual" }],
                      false
                    );
                    if (r.conflicts?.length) {
                      alert("存在冲突，未应用");
                    }
                    await onRefresh();
                  })
                }
              >
                应用
              </button>
            </div>
          </div>
        )}
      </div>

      {editing !== undefined && (
        <CharacterProfileModal
          project={project}
          initial={editing}
          others={project.characters.filter((c) => !editing || c.id !== editing.id)}
          onClose={() => setEditing(undefined)}
          onSaved={onRefresh}
        />
      )}
    </div>
  );
}
