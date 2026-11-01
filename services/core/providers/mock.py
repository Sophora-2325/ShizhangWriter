from __future__ import annotations

from decimal import Decimal
from typing import Iterator

from .base import ChatRequest, ChatResponse, ModelCapabilities


class MockProvider:
    """Canned responses for tests and for users who explicitly pick the mock provider."""

    name = "mock"
    display = "Mock"

    def capabilities(self, model: str) -> ModelCapabilities:
        return {
            "structured_output": "json_object",
            "thinking": False,
            "streaming": True,
            "prefix_completion": False,
            "vision": False,
            "max_context": 32_000,
            "max_output": 4_096,
        }

    def estimate_cost(self, model: str, input_tokens: int, output_tokens: int, cached_tokens: int = 0) -> Decimal:
        return Decimal("0")

    def list_models(self) -> list[str]:
        return ["mock"]

    def chat(self, req: ChatRequest) -> ChatResponse:
        purpose = req.get("purpose", "")
        if purpose == "body_revision":
            content = '{"patches":[]}'
        elif purpose == "proposal":
            content = ('{"plot_outline":"沿本章节奏展开会面与对峙，以尚未决定的交易收束。",'
                '"proposed_plan":{"scene_goal":"推进主线冲突","conflict":"对手施压","stakes":"失去线索"},'
                '"questions":[{"question":"交易的筹码是什么？","why":"决定对峙的具体内容",'
                '"options":["一封来信","一件关键物品"],"related_field":"conflict"}]}')
        elif purpose == "proposal_check":
            content = '{"status":"passed","issues":[]}'
        elif purpose == "rhythm_alignment":
            content = '{"status":"aligned","issues":[]}'
        elif purpose == "note_distill":
            # 笔记提炼：固定、可断言的「本章写作参考」，明确区分构想与事实
            content = (
                '{"reference":"构想：主角是否开门尚未决定；事实：雨夜来客已到门外，'
                '双方还没有交谈。写作时只使用笔记里的信息，不扩写剧情。"}'
            )
        elif purpose == "skill_distill":
            content = (
                '{"name":"示例技法","description":"从参考文章提炼的写作技法（模拟）",'
                '"applies_to":"generate","scenes":[],'
                '"instructions":"- 每段只推进一件事。\\n- 用具体动作代替形容词。\\n'
                '- 对白里不直接说出情绪，让读者从动作里读到。"}'
            )
        elif req.get("json_mode") or "json" in (req["messages"][-1].get("content", "").lower()):
            if purpose == "plan" or "ChapterPlan" in str(req["messages"]):
                content = (
                    '{"scene_goal":"推进主线冲突","conflict":"对手施压","stakes":"失去线索",'
                    '"present_character_ids":[],"required_threads":["main"],'
                    '"must_include":[],"must_not":["替主角做决定"],"target_word_count":1500,'
                    '"open_decision_for_user":"主角是否接受交易"}'
                )
            elif purpose == "extract" or "ChapterSummary" in str(req["messages"]):
                # evidence 必须是 prompt 正文里真实存在的短片段，避免 mock 路径误报未命中
                blob = req["messages"][-1].get("content", "") if req.get("messages") else ""
                ev = "雨还在下"
                marker = "正文："
                if marker in blob:
                    body_part = blob.split(marker, 1)[-1].strip()
                    # 取首段里一段连续汉字/标点（≤20）
                    run = []
                    for ch in body_part:
                        if ch in "\r\n":
                            if run:
                                break
                            continue
                        run.append(ch)
                        if len(run) >= 12:
                            break
                    cand = "".join(run).strip()
                    if len(cand) >= 2:
                        ev = cand[:40]
                # 转义 JSON 字符串
                ev_json = ev.replace("\\", "\\\\").replace('"', '\\"')
                content = (
                    '{"one_line":"本章发生了对峙","beats":[{"order":1,"what_happened":"双方会面",'
                    f'"who":[],"consequence":"僵持","evidence":"{ev_json}"}}],'
                    '"state_changes":[],'
                    '"new_facts":[],"open_hooks":["交易未决"],"continuity_flags":[],'
                    '"protagonist_decision_from_user":true}'
                )
            else:
                content = '{"flags":[],"passed":true}'
        else:
            prefix = ""
            for m in req["messages"]:
                if m.get("role") == "assistant" and m.get("prefix"):
                    prefix = m.get("content", "")
            content = (
                prefix
                + "雨还在下。他把杯子放回桌面，慢了半拍。\n\n"
                "「你来晚了。」对面的人说。\n\n"
                "他没有接话，只看着窗外那串模糊的数字。空气里有潮气和咖啡渣的味道。"
                "这件事还没到他做决定的时候——至少不是现在。"
            )
        return {
            "content": content,
            "model": req.get("model", "mock"),
            "input_tokens": 100,
            "output_tokens": 200,
            "cached_tokens": 50,
            "latency_ms": 10.0,
            "cost_usd": 0.0,
            "price_known": True,
        }

    def stream(self, req: ChatRequest) -> Iterator[str]:
        text = self.chat(req)["content"]
        for i in range(0, len(text), 20):
            yield text[i : i + 20]
