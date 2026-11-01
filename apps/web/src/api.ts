const BASE =
  import.meta.env.VITE_API_BASE ||
  (typeof window !== "undefined" &&
  (window as unknown as { __TAURI_INTERNALS__?: unknown }).__TAURI_INTERNALS__
    ? "http://127.0.0.1:18765"
    : "/api");


async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { "Content-Type": "application/json", ...(init?.headers || {}) },
    ...init,
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const detail = (data as { detail?: unknown }).detail;
    const msg =
      typeof detail === "string"
        ? detail
        : detail && typeof detail === "object" && "message" in (detail as object)
          ? String((detail as { message: string }).message)
          : res.statusText;
    const err = new Error(msg) as Error & { status: number; detail: unknown };
    err.status = res.status;
    err.detail = detail;
    throw err;
  }
  return data as T;
}

export type Phase =
  | "SETUP"
  | "PLAN"
  | "GENERATE"
  | "EXTRACT"
  | "GATE"
  | "REFINE"
  | "REPLAN";

export interface Relation {
  target_id: string;
  type: string;
  label: string;
  intensity: number;
}

export interface Character {
  id: string;
  name: string;
  age: string;
  personality: string;
  appearance: string;
  outfit: string;
  recent_thoughts: string;
  role: string;
  public_face: string;
  secret: string;
  goals: string[];
  join_reason: string;
  voice: string;
  relations: Relation[];
  known_facts: string[];
  status: Record<string, unknown>;
  notes: string;
}

export type CharacterUpsert = Omit<Character, "id"> & { id?: string };


export interface PlotThread {
  id: string;
  name: string;
  kind: string;
  state: string;
  user_directive: string;
  next_milestone: string;
  priority: number;
}

export interface StyleProfile {
  pov: string;
  tense: string;
  banned_words: string[];
  reference_samples: string[];
  compiled_system_constraints: string;
  metrics: Record<string, number>;
}

export type LoreCategory = "world" | "geo" | "faction" | "rule" | "history" | "other";

/** 一条背景设定；hard 只对 category === "rule" 有意义。 */
export interface LoreEntry {
  id: string;
  category: LoreCategory;
  title: string;
  content: string;
  hard: boolean;
}

export interface Project {
  id: string;
  title: string;
  phase: Phase;
  current_chapter: number;
  entries: LoreEntry[];
  characters: Character[];
  threads: PlotThread[];
  forbidden: string[];
  style: StyleProfile;
  offline_mode: boolean;
  provider: string;
  estimated_cost_usd: number;
}

export interface ChapterPlan {
  scene_goal: string;
  conflict: string;
  stakes: string;
  present_character_ids: string[];
  required_threads: string[];
  must_include: string[];
  must_not: string[];
  target_word_count: number;
  open_decision_for_user?: string | null;
}

export interface Chapter {
  no: number;
  title: string;
  status: string;
  plan?: ChapterPlan | null;
  body_text: string;
  word_count: number;
  summary_status?: "pending" | "extracting" | "ready" | "failed" | "stale";
  summary_error?: string;
  upstream_changed?: number[];
  plan_upstream_changed?: number[];
  summary?: {
    one_line: string;
    beats: { order: number; what_happened: string; evidence: string }[];
    continuity_flags: string[];
  } | null;
  gate?: {
    satisfied: boolean;
    dwell_seconds: number;
    scroll_ratio: number;
  } | null;
  refinement?: {
    next_chapter_intent: string;
    user_annotations: string;
  } | null;
  consistency_report?: {
    passed: boolean;
    flags: { kind: string; severity: string; point: string }[];
  } | null;
  style_report?: {
    verdict: string;
    banned_phrase_hits: { phrase: string }[];
    rewrite_suggestions: string[];
  } | null;
}

export const api = {
  health: () => req<{ status: string }>("/health"),
  settings: () => req<Record<string, unknown>>("/settings"),
  updateSettings: (body: Record<string, unknown>) =>
    req("/settings", { method: "POST", body: JSON.stringify(body) }),
  cost: (projectId?: string) =>
    req<{ total_cost_usd: number; entries: unknown[] }>(
      projectId ? `/cost?project_id=${projectId}` : "/cost"
    ),
  listProjects: () => req<Project[]>("/projects"),
  createProject: (body: unknown) =>
    req<Project>("/project", { method: "POST", body: JSON.stringify(body) }),
  getProject: (id: string) => req<Project>(`/project/${id}`),
  patchProjectSettings: (id: string, body: unknown) =>
    req<Project>(`/project/${id}/settings`, {
      method: "PATCH",
      body: JSON.stringify(body),
    }),
  chapters: (id: string) => req<Chapter[]>(`/project/${id}/chapters`),
  chapter: (id: string, n: number) => req<Chapter>(`/project/${id}/chapter/${n}`),
  plan: (id: string, notes = "") =>
    req<Chapter>(`/project/${id}/plan`, {
      method: "POST",
      body: JSON.stringify({ user_notes: notes }),
    }),
  generate: (id: string, prefix = "", memoryPolicy: "extract" | "raw" = "extract") =>
    req<Chapter>(`/project/${id}/chapter`, {
      method: "POST",
      body: JSON.stringify({ prefix, confirm_plan: true, memory_policy: memoryPolicy }),
    }),
  finalize: (id: string, n: number) =>
    req<Chapter>(`/project/${id}/chapter/${n}/finalize`, { method: "POST" }),
  extract: (id: string, n: number) =>
    req<Chapter>(`/project/${id}/chapter/${n}/extract`, { method: "POST" }),
  gate: (id: string, n: number, body: { dwell_seconds: number; scroll_ratio: number; method: string }) =>
    req(`/project/${id}/chapter/${n}/gate`, {
      method: "POST",
      body: JSON.stringify(body),
    }),
  refine: (id: string, n: number, body: unknown) =>
    req<Project>(`/project/${id}/chapter/${n}/refine`, {
      method: "POST",
      body: JSON.stringify(body),
    }),
  rollback: (id: string, n: number) =>
    req<Project>(`/project/${id}/rollback/${n}`, { method: "POST" }),
  bible: (id: string, edits: unknown[], preview_only = false) =>
    req<{ preview: unknown[]; conflicts: unknown[]; applied: boolean; project?: Project }>(
      `/project/${id}/bible`,
      { method: "POST", body: JSON.stringify({ edits, preview_only }) }
    ),
  listCharacters: (id: string) => req<Character[]>(`/project/${id}/characters`),
  upsertCharacter: (id: string, body: CharacterUpsert) =>
    req<Character>(`/project/${id}/characters`, {
      method: "POST",
      body: JSON.stringify(body),
    }),
  deleteCharacter: (id: string, charId: string) =>
    req<Project>(`/project/${id}/characters/${charId}`, { method: "DELETE" }),
  export: (id: string, fmt: string) =>
    req<{ path: string }>(`/project/${id}/export?fmt=${fmt}`),
  styleRewrite: (id: string, n: number) =>
    req<Chapter>(`/project/${id}/chapter/${n}/style-rewrite`, { method: "POST" }),
  audit: (id: string, n: number, semantic = false) =>
    req<Chapter>(`/project/${id}/audit/${n}?semantic=${semantic}`, { method: "POST" }),
  threads: (id: string) => req<PlotThread[]>(`/project/${id}/threads`),
};
