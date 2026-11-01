import { useEffect, useMemo, useState } from "react";
import { api, Character, CharacterUpsert, Project } from "./api";

const emptyDraft = (): CharacterUpsert => ({
  name: "",
  age: "",
  personality: "",
  appearance: "",
  outfit: "",
  recent_thoughts: "",
  role: "",
  public_face: "",
  secret: "",
  goals: [],
  join_reason: "",
  voice: "",
  relations: [],
  known_facts: [],
  status: {},
  notes: "",
});

function fromCharacter(c: Character): CharacterUpsert {
  return {
    id: c.id,
    name: c.name || "",
    age: c.age || "",
    personality: c.personality || "",
    appearance: c.appearance || "",
    outfit: c.outfit || "",
    recent_thoughts: c.recent_thoughts || "",
    role: c.role || "",
    public_face: c.public_face || "",
    secret: c.secret || "",
    goals: c.goals || [],
    join_reason: c.join_reason || "",
    voice: c.voice || "",
    relations: c.relations || [],
    known_facts: c.known_facts || [],
    status: c.status || {},
    notes: c.notes || "",
  };
}

export function CharacterProfileModal({
  project,
  initial,
  others,
  onClose,
  onSaved,
}: {
  project: Project;
  initial: Character | null; // null = new
  others: Character[];
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const [draft, setDraft] = useState<CharacterUpsert>(
    initial ? fromCharacter(initial) : emptyDraft()
  );
  const [goalsText, setGoalsText] = useState(
    initial?.goals?.join("\n") || ""
  );
  const [relText, setRelText] = useState(() => {
    if (!initial?.relations?.length) return "";
    return initial.relations
      .map((r) => {
        const name = others.find((o) => o.id === r.target_id)?.name || r.target_id;
        return `${name}：${r.label || r.type || ""}`;
      })
      .join("\n");
  });
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(false);

  const title = useMemo(
    () => (initial ? `个人资料 · ${initial.name || "未命名"}` : "个人资料 · 新人物"),
    [initial]
  );

  useEffect(() => {
    setDraft(initial ? fromCharacter(initial) : emptyDraft());
    setGoalsText(initial?.goals?.join("\n") || "");
  }, [initial?.id]);

  function setField<K extends keyof CharacterUpsert>(key: K, value: CharacterUpsert[K]) {
    setDraft((d) => ({ ...d, [key]: value }));
  }

  async function save() {
    setBusy(true);
    setErr("");
    try {
      const goals = goalsText
        .split("\n")
        .map((s) => s.trim())
        .filter(Boolean);
      const relations = relText
        .split("\n")
        .map((line) => line.trim())
        .filter(Boolean)
        .map((line) => {
          const [left, ...rest] = line.split("：");
          const label = rest.join("：").trim() || line;
          const nameOrId = (left || "").trim();
          const hit = others.find((o) => o.name === nameOrId || o.id === nameOrId);
          return {
            target_id: hit?.id || nameOrId,
            type: "related",
            label,
            intensity: 0.5,
          };
        });

      await api.upsertCharacter(project.id, {
        ...draft,
        goals,
        relations,
      });
      await onSaved();
      onClose();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal profile-modal" onClick={(e) => e.stopPropagation()}>
        <h2>{title}</h2>
        {err && <div className="error">{err}</div>}

        <div className="profile-grid">
          <fieldset>
            <legend>必填</legend>
            <div className="field">
              <label>姓名 *</label>
              <input value={draft.name} onChange={(e) => setField("name", e.target.value)} />
            </div>
            <div className="field">
              <label>年龄 *</label>
              <input value={draft.age} onChange={(e) => setField("age", e.target.value)} />
            </div>
            <div className="field">
              <label>性格 *</label>
              <textarea
                rows={2}
                value={draft.personality}
                onChange={(e) => setField("personality", e.target.value)}
              />
            </div>
            <div className="field">
              <label>相貌 *</label>
              <textarea
                rows={2}
                value={draft.appearance}
                onChange={(e) => setField("appearance", e.target.value)}
              />
            </div>
            <div className="field">
              <label>着装 *</label>
              <textarea
                rows={2}
                value={draft.outfit}
                onChange={(e) => setField("outfit", e.target.value)}
              />
            </div>
            <div className="field">
              <label>最近所想 *</label>
              <textarea
                rows={3}
                value={draft.recent_thoughts}
                onChange={(e) => setField("recent_thoughts", e.target.value)}
              />
            </div>
          </fieldset>

          <fieldset>
            <legend>可选</legend>
            <div className="field">
              <label>身份 / 角色定位</label>
              <input value={draft.role} onChange={(e) => setField("role", e.target.value)} />
            </div>
            <div className="field">
              <label>人物关系（每行：对方姓名：关系说明）</label>
              <textarea
                rows={3}
                value={relText}
                onChange={(e) => setRelText(e.target.value)}
                placeholder={"莉奈：村里唯一敢顶嘴的人\n加尔德：像叔叔一样的守村人"}
              />
            </div>
            <div className="field">
              <label>长久目标（每行一条）</label>
              <textarea rows={3} value={goalsText} onChange={(e) => setGoalsText(e.target.value)} />
            </div>
            <div className="field">
              <label>出场原因</label>
              <textarea
                rows={2}
                value={draft.join_reason}
                onChange={(e) => setField("join_reason", e.target.value)}
              />
            </div>
            <div className="field">
              <label>声口</label>
              <input value={draft.voice} onChange={(e) => setField("voice", e.target.value)} />
            </div>
            <div className="field">
              <label>秘密</label>
              <textarea
                rows={2}
                value={draft.secret}
                onChange={(e) => setField("secret", e.target.value)}
              />
            </div>
            <div className="field">
              <label>备注</label>
              <textarea
                rows={2}
                value={draft.notes}
                onChange={(e) => setField("notes", e.target.value)}
              />
            </div>
          </fieldset>
        </div>

        <div className="toolbar" style={{ border: "none", padding: "12px 0 0" }}>
          <button type="button" onClick={onClose} disabled={busy}>
            取消
          </button>
          <button type="button" className="primary" onClick={save} disabled={busy}>
            保存
          </button>
        </div>
      </div>
    </div>
  );
}
