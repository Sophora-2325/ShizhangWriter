import { useState } from "react";
import { api, Project } from "./api";

export function CreateProjectModal({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (p: Project) => void;
}) {
  const [title, setTitle] = useState("未命名小说");
  const [background, setBackground] = useState("");
  const [sample, setSample] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");

  async function submit() {
    setBusy(true);
    setErr("");
    try {
      const p = await api.createProject({
        title,
        genre: ["现实主义"],
        tone: "克制",
        characters: [],
        entries: background
          ? [{ id: "", category: "world", title: "", content: background, hard: false }]
          : [],
        forbidden: [],
        style: {
          pov: "third_limited",
          tense: "past",
          sentence_length: "medium",
          diction: [],
          banned_words: [],
          preferred_replacements: {},
          reference_samples: sample ? [sample] : [],
          compiled_system_constraints: "",
          punctuation_policy: {},
          metrics: {},
        },
        threads: [
          {
            id: "main",
            name: "主线",
            kind: "main",
            state: "active",
            user_directive: background || "推进主线",
            next_milestone: "",
            priority: 1,
          },
        ],
        npc_auto_act: false,
      });
      onCreated(p);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h2>新建项目</h2>
        {err && <div className="error">{err}</div>}
        <div className="field">
          <label>标题</label>
          <input value={title} onChange={(e) => setTitle(e.target.value)} />
        </div>
        <div className="field">
          <label>开篇背景（可选，会成为一条「世界观」条目）</label>
          <textarea rows={3} value={background} onChange={(e) => setBackground(e.target.value)} />
        </div>
        <div className="muted" style={{ marginBottom: 8 }}>
          人物请在右侧「人物」中添加（姓名、年龄、性格、相貌、着装、最近所想为必填）。
        </div>
        <div className="field">
          <label>文风范文（可选，500–2000 字更佳）</label>
          <textarea rows={4} value={sample} onChange={(e) => setSample(e.target.value)} />
        </div>
        <div className="toolbar" style={{ border: "none", padding: 0 }}>
          <button type="button" onClick={onClose}>
            取消
          </button>
          <button type="button" className="primary" disabled={busy} onClick={submit}>
            创建
          </button>
        </div>
      </div>
    </div>
  );
}
