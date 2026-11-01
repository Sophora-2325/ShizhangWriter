import { useEffect, useRef, useState } from "react";
import { api, Chapter, Project } from "./api";
import { BodyEditor } from "./BodyEditor";

export function CenterPane({
  project,
  chapter,
  busy,
  onRefresh,
  withBusy,
}: {
  project: Project;
  chapter: Chapter | null;
  busy: boolean;
  onRefresh: () => Promise<void>;
  withBusy: <T>(fn: () => Promise<T>) => Promise<T | undefined>;
}) {
  const [prefix, setPrefix] = useState("");
  const [intent, setIntent] = useState("");
  const [notes, setNotes] = useState("");
  const [editing, setEditing] = useState(false);
  const [memoryPolicy, setMemoryPolicy] = useState<"extract" | "raw">("extract");
  const openedAt = useRef(Date.now());
  const maxScroll = useRef(0);
  const readerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    openedAt.current = Date.now();
    maxScroll.current = 0;
    setIntent(chapter?.refinement?.next_chapter_intent || "");
    setNotes(chapter?.refinement?.user_annotations || "");
    setPrefix("");
    setEditing(false);
  }, [chapter?.no]);

  function onScroll() {
    const el = readerRef.current;
    if (!el) return;
    const ratio =
      el.scrollHeight <= el.clientHeight
        ? 1
        : (el.scrollTop + el.clientHeight) / el.scrollHeight;
    maxScroll.current = Math.max(maxScroll.current, ratio);
  }

  if (!chapter) {
    return (
      <div className="pane">
        <div className="pane-header">正文</div>
        <div className="empty">选择或规划一个章节</div>
      </div>
    );
  }

  const dwell = () => (Date.now() - openedAt.current) / 1000;

  return (
    <div className="pane" style={{ display: "flex", flexDirection: "column" }}>
      <div className="pane-header">
        第{chapter.no}章 {chapter.title} · {chapter.word_count} 字
        {chapter.style_report && (
          <span className="muted"> · 文风 {chapter.style_report.verdict}</span>
        )}
      </div>
      <div className="toolbar">
        <button
          type="button"
          className="primary"
          disabled={busy || !chapter.plan}
          onClick={() =>
            withBusy(async () => {
              await api.generate(project.id, prefix, memoryPolicy);
              await onRefresh();
            })
          }
        >
          生成正文
        </button>
        <select aria-label="生成时使用的历史记忆" value={memoryPolicy} disabled={busy}
          onChange={e => setMemoryPolicy(e.target.value as "extract" | "raw")}>
          <option value="extract">自动补齐摘要（可能产生模型费用）</option>
          <option value="raw">使用近期原文（按长度截取）</option>
        </select>
        <button
          type="button"
          disabled={busy || !chapter.body_text}
          onClick={() =>
            withBusy(async () => {
              await api.finalize(project.id, chapter.no);
              await onRefresh();
            })
          }
        >
          定稿+提取
        </button>
        <button type="button" disabled={busy || !chapter.body_text} onClick={() => withBusy(async () => {
          try { await api.extract(project.id, chapter.no); }
          finally { await onRefresh(); }
        })}>提取 / 刷新摘要</button>
        <button
          type="button"
          disabled={busy || !(chapter.status === "final" || chapter.status === "drafted")}
          onClick={() =>
            withBusy(async () => {
              await api.gate(project.id, chapter.no, {
                dwell_seconds: dwell(),
                scroll_ratio: Math.max(maxScroll.current, chapter.body_text ? 0.5 : 0),
                method: "manual",
              });
              await onRefresh();
            })
          }
        >
          确认读完
        </button>
        <button type="button" disabled={busy} onClick={() => setEditing((v) => !v)}>
          {editing ? "阅读" : "细修正文"}
        </button>
        <button
          type="button"
          disabled={busy || !chapter.body_text}
          onClick={() =>
            withBusy(async () => {
              await api.styleRewrite(project.id, chapter.no);
              await onRefresh();
            })
          }
        >
          按范本重写
        </button>
        <button
          type="button"
          disabled={busy || !chapter.body_text}
          onClick={() =>
            withBusy(async () => {
              await api.audit(project.id, chapter.no, false);
              await onRefresh();
            })
          }
        >
          审计
        </button>
      </div>

      {chapter.plan && (
        <div className="toolbar muted" style={{ fontSize: 12 }}>
          计划：{chapter.plan.scene_goal} ／ 冲突：{chapter.plan.conflict}
          {chapter.plan.open_decision_for_user && (
            <span className="error"> ／ 待用户决策：{chapter.plan.open_decision_for_user}</span>
          )}
        </div>
      )}

      <div style={{ padding: "6px 8px", borderBottom: "1px solid var(--border)" }}>
        <label className="muted">前缀补全（可选，写开头两三句）</label>
        <textarea
          rows={2}
          value={prefix}
          onChange={(e) => setPrefix(e.target.value)}
          placeholder="自己写开头，模型续写…"
        />
      </div>

      <div
        className="pane-body"
        ref={readerRef}
        onScroll={onScroll}
        style={{ flex: 1 }}
      >
        {editing ? (
          <BodyEditor initial={chapter.body_text} />
        ) : (
          <div className="reader">{chapter.body_text || "（尚未生成）"}</div>
        )}
        {chapter.summary_status && chapter.summary_status !== "ready" && (
          <div role="status" style={{ padding: 12 }}>
            {{ pending: "摘要待提取", extracting: "正在提取摘要", stale: "正文已修改，旧记忆与检查已过期",
               failed: "摘要提取失败，正文已保存，可重试" }[chapter.summary_status]}
          </div>
        )}
        {!!(chapter.upstream_changed?.length || chapter.plan_upstream_changed?.length) && (
          <div role="status" style={{ padding: 12 }}>上游章节已修改，请复核本章计划与记忆。</div>
        )}
        {chapter.summary && (
          <div className="section" style={{ padding: 12, borderTop: "1px solid var(--border)" }}>
            <h3>摘要</h3>
            <div>{chapter.summary.one_line}</div>
            <ul>
              {chapter.summary.beats?.map((b) => (
                <li key={b.order}>
                  {b.what_happened}
                  {b.evidence && <span className="muted"> 「{b.evidence}」</span>}
                </li>
              ))}
            </ul>
            {chapter.summary.continuity_flags?.length > 0 && (
              <div className="error">{chapter.summary.continuity_flags.join("；")}</div>
            )}
          </div>
        )}
        {chapter.consistency_report && chapter.consistency_report.flags.length > 0 && (
          <div className="section" style={{ padding: 12 }}>
            <h3>一致性</h3>
            {chapter.consistency_report.flags.map((f, i) => (
              <div key={i} className={f.severity === "fail" ? "error" : "muted"}>
                [{f.severity}] {f.point}
              </div>
            ))}
          </div>
        )}
        {chapter.style_report && chapter.style_report.rewrite_suggestions.length > 0 && (
          <div className="section muted" style={{ padding: 12 }}>
            {chapter.style_report.rewrite_suggestions.join("；")}
          </div>
        )}
      </div>

      <div className="refine-box">
        <div className="muted">
          闸门：{chapter.gate?.satisfied ? "已确认读完" : "未确认"} · 细修提交后才能生成下一章
        </div>
        <label>本章评注</label>
        <textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
        <label>下一章脉络意图（必填）</label>
        <textarea rows={2} value={intent} onChange={(e) => setIntent(e.target.value)} />
        <button
          type="button"
          className="primary"
          style={{ marginTop: 6 }}
          disabled={busy || !chapter.gate?.satisfied}
          onClick={() =>
            withBusy(async () => {
              await api.refine(project.id, chapter.no, {
                chapter_no: chapter.no,
                user_annotations: notes,
                next_chapter_intent: intent,
                thread_updates: [],
                bible_edits: [],
                constraints_for_next: [],
              });
              await onRefresh();
            })
          }
        >
          提交细修并解锁下一章
        </button>
      </div>
    </div>
  );
}
