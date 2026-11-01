import { Chapter } from "./api";

export function ChapterList({
  chapters,
  selectedNo,
  onSelect,
  busy,
  onPlan,
  onRollback,
}: {
  chapters: Chapter[];
  selectedNo: number | null;
  onSelect: (n: number) => void;
  busy: boolean;
  onPlan: () => void;
  onRollback: () => void;
}) {
  return (
    <div className="pane">
      <div className="pane-header">章节</div>
      <div className="toolbar">
        <button type="button" className="primary" disabled={busy} onClick={onPlan}>
          规划下一章
        </button>
        <button type="button" disabled={busy || selectedNo == null} onClick={onRollback}>
          回滚
        </button>
      </div>
      <div className="pane-body">
        {chapters.length === 0 && <div className="muted">尚无章节，点击「规划下一章」</div>}
        {chapters.map((c) => (
          <div
            key={c.no}
            className={`chapter-item ${selectedNo === c.no ? "active" : ""}`}
            onClick={() => onSelect(c.no)}
          >
            <span>
              {c.no}. {c.title || "未命名"}
            </span>
            <span className="status">{c.status}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
