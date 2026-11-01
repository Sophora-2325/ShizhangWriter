from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Any, Optional, Literal
from uuid import uuid4

from pydantic import BaseModel, Field, model_validator, field_validator


class Phase(str, Enum):
    SETUP = "SETUP"
    PLAN = "PLAN"
    GENERATE = "GENERATE"
    EXTRACT = "EXTRACT"
    GATE = "GATE"
    REFINE = "REFINE"
    REPLAN = "REPLAN"


class ChapterStatus(str, Enum):
    planned = "planned"
    drafted = "drafted"
    final = "final"
    superseded = "superseded"


# 背景设定的分类；顺序即界面与提示词里的分组顺序
LORE_CATEGORIES = ("world", "geo", "faction", "rule", "history", "item", "other")
LORE_CATEGORY_LABELS = {
    "world": "世界观",
    "geo": "地理",
    "faction": "势力",
    "rule": "规则",
    "history": "历史",
    "item": "物件",
    "other": "其他",
}


class LoreEntry(BaseModel):
    """一条背景设定（世界书式条目）。hard 仅对 category == "rule" 有意义。"""

    id: str = ""
    category: str = "world"  # 取值见 LORE_CATEGORIES
    title: str = ""  # 可空；界面与提示词用 content 前 24 字兜底
    content: str = ""
    hard: bool = False


class Relation(BaseModel):
    target_id: str
    type: str = ""
    label: str = ""  # 对人关系描述，如「青梅竹马」
    intensity: float = 0.5


class Character(BaseModel):
    id: str
    # —— 必填个人资料 ——
    name: str
    age: str = ""
    gender: str = ""
    personality: str = ""
    appearance: str = ""
    outfit: str = ""
    hobbies: str = ""
    organization: str = ""
    thinking_habits: str = ""
    recent_thoughts: str = ""
    # —— 可选 ——
    role: str = ""
    public_face: str = ""
    secret: str = ""
    goals: list[str] = Field(default_factory=list)  # 长久目标
    join_reason: str = ""  # 出场原因等
    voice: str = ""
    relations: list[Relation] = Field(default_factory=list)
    known_facts: list[str] = Field(default_factory=list)
    status: dict[str, Any] = Field(default_factory=dict)  # injury, location, etc.
    notes: str = ""  # 其它备注
    portrait: str = ""  # 作品文件夹内的相对路径，如 人物画像/abc.png

    def missing_required(self) -> list[str]:
        # 新卡片只有姓名是硬性要求；其余资料可以留空，之后再补。
        required = {"name": self.name}
        return [k for k, v in required.items() if not str(v).strip()]


class CharacterUpsert(BaseModel):
    """创建/更新人物；id 为空则新建。

    chapter_no / chapter_action 是「人物卡」与「本章行动」的组合保存：
    - chapter_action 省略或 null：不修改该章已有行动；
    - chapter_action == ""：明确清空；
    - 其他字符串：写入该人物在该章的待执行行动。
    """
    id: Optional[str] = None
    name: str = ""
    age: str = ""
    gender: str = ""
    personality: str = ""
    appearance: str = ""
    outfit: str = ""
    hobbies: str = ""
    organization: str = ""
    thinking_habits: str = ""
    recent_thoughts: str = ""
    role: str = ""
    public_face: str = ""
    secret: str = ""
    goals: list[str] = Field(default_factory=list)
    join_reason: str = ""
    voice: str = ""
    relations: list[Relation] = Field(default_factory=list)
    known_facts: list[str] = Field(default_factory=list)
    status: dict[str, Any] = Field(default_factory=dict)
    notes: str = ""
    portrait: str = ""
    chapter_no: Optional[int] = None
    chapter_action: Optional[str] = None


class StyleProfile(BaseModel):
    pov: str = "third_limited"
    tense: str = "past"
    sentence_length: str = "medium"
    diction: list[str] = Field(default_factory=list)
    banned_words: list[str] = Field(default_factory=list)
    preferred_replacements: dict[str, str] = Field(default_factory=dict)
    reference_samples: list[str] = Field(default_factory=list)
    compiled_system_constraints: str = ""
    punctuation_policy: dict[str, Any] = Field(default_factory=dict)
    metrics: dict[str, float] = Field(default_factory=dict)


class ThreadMilestone(BaseModel):
    id: str = Field(default_factory=lambda: uuid4().hex[:12])
    name: str
    description: str = ""
    completed: bool = False

    @field_validator("name")
    @classmethod
    def require_name(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("里程碑名称不能为空")
        return value.strip()


class PlotThread(BaseModel):
    id: str
    name: str
    kind: str = "main"  # main|sub|romance|mystery
    state: str = "seed"  # seed|active|climax|resolved|abandoned
    user_directive: str = ""
    next_milestone: str = ""
    priority: int = 1
    time_position: Literal["past", "present", "future"] = "present"
    tone: str = ""
    milestones: list[ThreadMilestone] = Field(default_factory=list)
    misc: str = ""  # 作者补充说明，随脉络保存并提供给 AI

    @model_validator(mode="before")
    @classmethod
    def migrate_milestone(cls, value: Any) -> Any:
        if isinstance(value, dict) and "milestones" not in value:
            value = dict(value)
            text = str(value.get("next_milestone") or "").strip()
            # Stable across reads of an old snapshot before its first explicit save.
            value["milestones"] = [{"id": "legacy-next", "name": text}] if text else []
        return value

    @model_validator(mode="after")
    def sync_next_milestone(self) -> PlotThread:
        self.next_milestone = next((m.name for m in self.milestones if not m.completed), "")
        return self


class ChapterPlan(BaseModel):
    scene_goal: str = ""
    conflict: str = ""
    stakes: str = ""
    present_character_ids: list[str] = Field(default_factory=list)
    required_threads: list[str] = Field(default_factory=list)
    must_include: list[str] = Field(default_factory=list)
    must_not: list[str] = Field(default_factory=list)
    target_word_count: int = 3000
    open_decision_for_user: Optional[str] = None
    free_text: str = ""  # 用户自由书写的规划，生成时与表单字段一起交给模型
    misc: str = ""  # 作者手写的本章补充说明；重新规划时原样保留
    scene_types: list[str] = Field(default_factory=list)  # 本章戏份，见 skills.SCENES


class ProposalPlanPatch(BaseModel):
    """Only these fields can be proposed. None means no scalar suggestion."""
    model_config = {"extra": "forbid"}
    scene_goal: str | None = None
    conflict: str | None = None
    stakes: str | None = None
    target_word_count: int | None = Field(default=None, ge=1, le=100000)
    open_decision_for_user: str | None = None
    must_include: list[str] = Field(default_factory=list)
    must_not: list[str] = Field(default_factory=list)
    present_character_ids: list[str] = Field(default_factory=list)
    required_threads: list[str] = Field(default_factory=list)
    scene_types: list[str] = Field(default_factory=list)


class ProposalQuestionOutput(BaseModel):
    question: str = Field(min_length=1, max_length=120)
    why: str = Field(default="", max_length=300)
    options: list[str] = Field(default_factory=list, max_length=3)
    related_field: Literal["", "scene_goal", "conflict", "stakes", "must_include", "must_not", "open_decision_for_user"] = ""

    @field_validator("question")
    @classmethod
    def nonblank(cls, value):
        if not value.strip():
            raise ValueError("问题不能为空")
        return value.strip()

    @field_validator("options")
    @classmethod
    def distinct_options(cls, value):
        if any(not x.strip() for x in value) or len(set(value)) != len(value):
            raise ValueError("选项须非空且不重复")
        return value


class ProposalQuestion(ProposalQuestionOutput):
    id: str = Field(default_factory=lambda: uuid4().hex)
    answer: str = Field(default="", max_length=4000)


class AuthorDecision(BaseModel):
    id: str = Field(default_factory=lambda: uuid4().hex)
    source_proposal_id: str
    question_id: str
    question: str
    answer: str = Field(default="", max_length=4000)
    related_field: str = ""


class ProposalOutput(BaseModel):
    plot_outline: str = Field(min_length=1, max_length=60000)
    proposed_plan: ProposalPlanPatch = Field(default_factory=ProposalPlanPatch)
    questions: list[ProposalQuestionOutput] = Field(default_factory=list, max_length=3)


class ProposalValidation(BaseModel):
    effective_input_hash: str
    payload_hash: str
    policy_version: str
    checked_at: datetime
    provider: str
    model: str
    result: Literal["passed", "mismatch", "unchecked", "failed"]
    usage: dict[str, Any] = Field(default_factory=dict)


class ChapterProposal(BaseModel):
    id: str = Field(default_factory=lambda: uuid4().hex)
    chapter_uid: str
    created_at: datetime
    plot_outline: str
    proposed_plan: ProposalPlanPatch = Field(default_factory=ProposalPlanPatch)
    questions: list[ProposalQuestion] = Field(default_factory=list, max_length=3)
    author_decisions: list[AuthorDecision] = Field(default_factory=list)
    provider: str = ""
    model: str = ""
    usage: dict[str, Any] = Field(default_factory=dict)
    generated_input_hash: str = ""
    bound_input_hash: str = ""
    used_answers_hash: str = ""
    source_manifest: dict[str, Any] = Field(default_factory=dict)
    validation: ProposalValidation | None = None
    adopted_at: datetime | None = None
    final_plan: ChapterPlan | None = None
    active_history_id: str = ""


class ProposalWorkspace(BaseModel):
    revision: int = Field(default=0, ge=0)
    draft: ChapterProposal | None = None
    adopted: ChapterProposal | None = None


class ProposalCommand(BaseModel):
    chapter_uid: str = Field(min_length=1, max_length=64, pattern=r"^[a-zA-Z0-9_-]+$")
    expected_revision: int = Field(ge=0)
    request_id: str = Field(min_length=1, max_length=100, pattern=r"^[a-zA-Z0-9_-]+$")
    input_token: str = ""
    mode: Literal["generate", "regenerate", "answers"] = "generate"
    answers: dict[str, str] = Field(default_factory=dict)
    decisions: dict[str, str] = Field(default_factory=dict)
    plot_outline: str | None = Field(default=None, max_length=60000)

    @field_validator("answers", "decisions")
    @classmethod
    def answer_limits(cls, value):
        if any(len(x) > 4000 for x in value.values()):
            raise ValueError("答案最多 4000 字")
        return value

    @field_validator("plot_outline")
    @classmethod
    def outline_or_none(cls, value):
        if value is None or len(value.strip()) == 0:
            return None
        return value


class Beat(BaseModel):
    order: int
    what_happened: str
    who: list[str] = Field(default_factory=list)
    consequence: str = ""
    evidence: str = ""  # quote from body <=40 chars


class NewFact(BaseModel):
    fact: str
    known_by: list[str] = Field(default_factory=list)


class ChapterSummary(BaseModel):
    one_line: str = ""
    beats: list[Beat] = Field(default_factory=list)
    state_changes: list[str] = Field(default_factory=list)
    new_facts: list[NewFact] = Field(default_factory=list)
    open_hooks: list[str] = Field(default_factory=list)
    continuity_flags: list[str] = Field(default_factory=list)
    protagonist_decision_from_user: Optional[bool] = None


class StateDelta(BaseModel):
    character_updates: dict[str, dict[str, Any]] = Field(default_factory=dict)
    thread_updates: dict[str, dict[str, Any]] = Field(default_factory=dict)
    facts_added: list[NewFact] = Field(default_factory=list)


class ConsistencyFlag(BaseModel):
    kind: str  # rule|state|semantic
    severity: str = "warn"  # info|warn|fail
    point: str
    evidence: str = ""
    suggestion: str = ""


class ConsistencyReport(BaseModel):
    flags: list[ConsistencyFlag] = Field(default_factory=list)
    passed: bool = True
    # 是否真的跑过语义审计（强模型）。只有 api_audit(semantic=True) 会置 True；
    # 旧数据没有这个字段 → False，用来区分「查过且干净」与「只跑过规则检查」。
    semantic_checked: bool = False


class StyleReport(BaseModel):
    banned_phrase_hits: list[dict[str, Any]] = Field(default_factory=list)
    ai_tic_findings: list[dict[str, Any]] = Field(default_factory=list)
    metrics_drift: dict[str, float] = Field(default_factory=dict)
    verdict: str = "pass"  # pass|marginal|fail
    rewrite_suggestions: list[str] = Field(default_factory=list)


class BibleEdit(BaseModel):
    path: str
    old: str = ""
    new: str = ""
    reason: str = ""


class ThreadUpdate(BaseModel):
    thread_id: str
    new_directive: str = ""
    new_state: Optional[str] = None


class Refinement(BaseModel):
    chapter_no: int
    user_annotations: str = ""
    thread_updates: list[ThreadUpdate] = Field(default_factory=list)
    bible_edits: list[BibleEdit] = Field(default_factory=list)
    next_chapter_intent: str = ""
    constraints_for_next: list[str] = Field(default_factory=list)


class ReviewDraft(BaseModel):
    """评注区尚未提交的草稿；提交「下一步」细修后清空。"""
    user_annotations: str = ""
    next_chapter_intent: str = ""


class ReadingGate(BaseModel):
    chapter_no: int
    opened_at: Optional[datetime] = None
    read_confirmed_at: Optional[datetime] = None
    dwell_seconds: float = 0.0
    scroll_ratio: float = 0.0
    method: str = "manual"  # manual|timed|quiz
    satisfied: bool = False


class CoverAsset(BaseModel):
    """作品内封面资源引用；空引用表示无封面。"""
    preview: str = ""
    original: str = ""


class ProjectInfoUpdate(BaseModel):
    genre: Optional[list[str]] = None
    tone: Optional[str] = None
    audience: Optional[str] = None
    cover: Optional[CoverAsset] = None


class ProjectCreate(BaseModel):
    title: str
    genre: list[str] = Field(default_factory=list)
    tone: str = ""
    audience: str = ""
    entries: list[LoreEntry] = Field(default_factory=list)
    characters: list[Character] = Field(default_factory=list)
    forbidden: list[str] = Field(default_factory=list)
    style: StyleProfile = Field(default_factory=StyleProfile)
    threads: list[PlotThread] = Field(default_factory=list)
    npc_auto_act: bool = False
    parent_dir: str = ""  # 作品文件夹建在 <parent_dir>/<作品名>


class Project(BaseModel):
    id: str
    title: str
    created_at: datetime
    phase: Phase = Phase.SETUP
    current_chapter: int = 0
    genre: list[str] = Field(default_factory=list)
    tone: str = ""
    audience: str = ""
    cover: CoverAsset = Field(default_factory=CoverAsset)
    entries: list[LoreEntry] = Field(default_factory=list)
    characters: list[Character] = Field(default_factory=list)
    forbidden: list[str] = Field(default_factory=list)
    style: StyleProfile = Field(default_factory=StyleProfile)
    threads: list[PlotThread] = Field(default_factory=list)
    npc_auto_act: bool = False
    offline_mode: bool = False
    provider: str = "inherit"  # inherit（跟随全局设置）|deepseek|local|mock
    estimated_cost_usd: float = 0.0
    root_path: str = ""  # 作品文件夹；为空表示尚未迁移的旧作品
    root_missing: bool = False


class ChapterLore(BaseModel):
    """某一章使用的设定快照；规划下一章时整体继承。"""
    entries: list[LoreEntry] = Field(default_factory=list)
    characters: list[Character] = Field(default_factory=list)
    threads: list[PlotThread] = Field(default_factory=list)


class ConstraintSegment(BaseModel):
    id: str = Field(default_factory=lambda: uuid4().hex, min_length=1)
    start: float = Field(ge=0, le=100, allow_inf_nan=False)
    end: float = Field(ge=0, le=100, allow_inf_nan=False)
    top: float = Field(default=10, ge=0, le=100, allow_inf_nan=False)
    bottom: float = Field(default=90, ge=0, le=100, allow_inf_nan=False)
    directive: str = ""

    @model_validator(mode="after")
    def positive_area(self):
        if self.end <= self.start or self.bottom <= self.top:
            raise ValueError("片段必须具有正的宽度和高度")
        self.directive = self.directive.replace("\r\n", "\n")
        return self


class ConstraintTrack(BaseModel):
    id: str = Field(default_factory=lambda: uuid4().hex, min_length=1)
    domain: Literal["thread", "character", "scene"]
    element_id: str = Field(min_length=1)
    name: str = ""
    weight: float = Field(default=0.5, ge=0.1, le=1, allow_inf_nan=False)
    segments: list[ConstraintSegment] = Field(default_factory=list)


class StackedConstraints(BaseModel):
    tracks: list[ConstraintTrack] = Field(default_factory=list)

    @model_validator(mode="after")
    def unique_elements_and_ids(self):
        keys = [(t.domain, t.element_id) for t in self.tracks]
        ids = [t.id for t in self.tracks] + [s.id for t in self.tracks for s in t.segments]
        if len(keys) != len(set(keys)) or len(ids) != len(set(ids)):
            raise ValueError("叠条元素或片段 ID 不可重复")
        return self


class BodyRevisionPatch(BaseModel):
    id: str = Field(default_factory=lambda: uuid4().hex)
    before: str = Field(min_length=1, max_length=12000)
    after: str = Field(max_length=12000)
    reason: str = Field(default="", max_length=2000)
    start: int = -1
    end: int = -1
    unavailable: str = ""


class BodyRevisionWorkspace(BaseModel):
    revision: int = 0
    instruction: str = ""
    selected_issues: list[str] = Field(default_factory=list)
    preview_id: str = ""
    input_token: str = ""
    patches: list[BodyRevisionPatch] = Field(default_factory=list)
    usage: dict = Field(default_factory=dict)


class BodyRevisionCommand(BaseModel):
    chapter_uid: str = Field(min_length=1, max_length=64)
    expected_revision: int = Field(ge=0)
    request_id: str = Field(min_length=1, max_length=100, pattern=r"^[a-zA-Z0-9_-]+$")
    input_token: str = ""
    instruction: str = Field(default="", max_length=12000)
    selected_issues: list[str] = Field(default_factory=list, max_length=100)
    preview_id: str = ""
    patch_ids: list[str] = Field(default_factory=list, max_length=100)
    body_text: str = ""
    body_rich: str = ""


class Chapter(BaseModel):
    no: int
    chapter_uid: str = Field(default_factory=lambda: uuid4().hex, pattern=r"^[a-zA-Z0-9_-]{1,64}$")
    proposal: ProposalWorkspace = Field(default_factory=ProposalWorkspace)
    body_revision: BodyRevisionWorkspace = Field(default_factory=BodyRevisionWorkspace)
    title: str = ""
    status: ChapterStatus = ChapterStatus.planned
    plan: Optional[ChapterPlan] = None
    stacked_constraints: StackedConstraints = Field(default_factory=StackedConstraints)
    stacked_weight_migration_pending: bool = False
    body_text: str = ""
    body_rich: str = ""  # 带格式正文（WPF FlowDocument XAML）；纯文字以 body_text 为准
    word_count: int = 0
    created_at: Optional[datetime] = None
    summary: Optional[ChapterSummary] = None
    state_delta: Optional[StateDelta] = None
    consistency_report: Optional[ConsistencyReport] = None
    # 「本章校验」的产物：核对计划里的 must_include / must_not / required_threads 有没有被正文照做。
    # 与 consistency_report 分开存，互不覆盖——审计问「正文和设定有没有冲突」，校验问「计划要求做到的事做到了没有」。
    plan_check: Optional[ConsistencyReport] = None
    style_report: Optional[StyleReport] = None
    gate: Optional[ReadingGate] = None
    refinement: Optional[Refinement] = None
    lore: Optional[ChapterLore] = None
    lore_from: Optional[int] = None  # 本章设定继承自哪一章
    # 作者为本章手写的「本章行动」：角色 id -> 待执行意图。只属于本章，不随章节继承。
    character_actions: dict[str, str] = Field(default_factory=dict)
    review: Optional[ReviewDraft] = None
    body_hash: str = ""
    derived_hashes: dict[str, str] = Field(default_factory=dict)
    stale_results: dict[str, Any] = Field(default_factory=dict)
    summary_status: str = "pending"  # pending|extracting|ready|failed|stale
    summary_error: str = ""
    upstream_changed: list[int] = Field(default_factory=list)
    plan_upstream_changed: list[int] = Field(default_factory=list)


class PlanRequest(BaseModel):
    user_notes: str = ""
    overrides: Optional[ChapterPlan] = None


class GenerateRequest(BaseModel):
    prefix: str = ""  # user-written opening for prefix completion
    confirm_plan: bool = True
    memory_policy: str = "extract"  # extract: 补齐后继续；raw: 明确使用原文


class BodyUpdate(BaseModel):
    body_text: str = ""
    body_rich: str = ""


class ChapterCreate(BaseModel):
    body_text: str = ""
    body_rich: str = ""
    plan: Optional[ChapterPlan] = None


class GateConfirmRequest(BaseModel):
    dwell_seconds: float = 0.0
    scroll_ratio: float = 0.0
    method: str = "manual"


class SettingsUpdate(BaseModel):
    provider: Optional[str] = None
    offline_mode: Optional[bool] = None
    deepseek_api_key: Optional[str] = None
    local_base_url: Optional[str] = None
    local_model: Optional[str] = None
    gate_mode: Optional[str] = None  # soft|behavior|quiz
    last_parent_dir: Optional[str] = None


class CostEntry(BaseModel):
    provider: str
    model: str
    input_tokens: int = 0
    output_tokens: int = 0
    cached_tokens: int = 0
    cost_usd: float = 0.0
    latency_ms: float = 0.0
    purpose: str = ""
