"""各写作平台的投稿入口与发布包要求（纯数据）。

国内写作平台没有公开的投稿 API，作家后台的地址也会随改版变动，所以这里只做一件事：
把「平台 → 后台地址 + 发布包该怎么排」写成一张表。生成逻辑在 `__init__.py`，
改动这张表不需要动任何代码。

`unverified=True` 的条目没能从官方文档确认（多半是中小书站），第一次使用时请核对一次；
地址失效也只需要改这一行。真打不开时对话框会给 `homepage` 兜底。

字段含义：
    format       发布包正文的文件格式：txt（导入类书站）/ md / html（粘贴类社区）
    indent       段首默认缩进：none（平台编辑器自己处理）/ full（两个全角空格）
    min_words    单章建议字数下限，只作界面提示，不拦人
    batch_import 是否支持整本导入（决定对话框里推不推荐「全书」范围）
"""

from __future__ import annotations

from dataclasses import dataclass, field

INDENT_NONE = "none"
INDENT_FULL = "full"

FORMAT_TXT = "txt"
FORMAT_MD = "md"
FORMAT_HTML = "html"


@dataclass(frozen=True)
class PlatformSpec:
    key: str
    name: str
    group: str
    backend_url: str
    homepage: str
    format: str = FORMAT_TXT
    indent: str = INDENT_NONE
    min_words: int = 0
    batch_import: bool = True
    unverified: bool = False
    notes: tuple[str, ...] = field(default_factory=tuple)

    @property
    def default_indent(self) -> str:
        return self.indent

    @property
    def with_head(self) -> bool:
        """粘贴类社区需要「书名 / 作者 / 简介」抬头；导入类书站只需要章节正文。"""
        return self.format != FORMAT_TXT


# (key, 名称, 一句话说明) —— 顺序即对话框里的分组顺序
PLATFORM_GROUPS: tuple[tuple[str, str, str], ...] = (
    ("yuewen", "阅文系", "签约制书站，单章字数越足越稳"),
    ("booksite", "其他书站", "多数支持整本 txt 导入"),
    ("community", "自媒体与社区", "粘贴富文本可以保留加粗与引用"),
)

_TXT_NOTES = (
    "整本导入时用「第N章」独立成行分段，平台的导入器就是按这个找章节的。",
    "发布包默认不加段首缩进，交给平台编辑器处理；要自带缩进可在对话框里改成「全角两格」。",
    "敏感词请用平台编辑器自带的检测过一遍，各站词库不一样，本工具不做替换。",
)


PLATFORMS: tuple[PlatformSpec, ...] = (
    PlatformSpec(
        key="qidian",
        name="起点中文网",
        group="yuewen",
        backend_url="https://write.qq.com/",
        homepage="https://www.qidian.com/",
        min_words=2000,
        notes=(
            "起点与 QQ阅读 共用「阅文作家专区」，在同一个后台发文即可，QQ阅读 是分发渠道。",
            "后台支持整本 txt / docx 导入，导入后每章会生成一条草稿，需要逐章确认再发布。",
            "签约作品单章建议 2000 字以上，低于 1000 字容易被判「内容过短」。",
        )
        + _TXT_NOTES,
    ),
    PlatformSpec(
        key="fanqie",
        name="番茄小说",
        group="yuewen",
        backend_url="https://fanqienovel.com/main/writer/",
        homepage="https://fanqienovel.com/",
        unverified=True,
        min_words=1000,
        notes=(
            "作家后台支持整本 txt 导入，按「第N章」自动切章。",
            "番茄对章节名有长度限制，太长的标题会被截断，建议 20 字以内。",
        )
        + _TXT_NOTES,
    ),
    PlatformSpec(
        key="jjwxc",
        name="晋江文学城",
        group="yuewen",
        backend_url="http://my.jjwxc.net/",
        homepage="https://www.jjwxc.net/",
        batch_import=False,
        min_words=2000,
        notes=(
            "晋江没有整本导入，需要逐章粘贴；本工具生成 txt 与「复制正文」都按这个流程走。",
            "标题里不要出现【】《》等符号，晋江的章节名会被这些字符影响排版。",
            "提交后进审核队列，审核期间不要反复改动同一章。",
        )
        + _TXT_NOTES,
    ),
    PlatformSpec(
        key="qimao",
        name="七猫中文网",
        group="booksite",
        backend_url="https://zuojia.qimao.com/",
        homepage="https://www.qimao.com/",
        unverified=True,
        min_words=1000,
        notes=("七猫系站点改版较频繁，投稿入口若打不开，用 homepage 从首页找「作家专区」。",)
        + _TXT_NOTES,
    ),
    PlatformSpec(
        key="zhangyue",
        name="掌阅",
        group="booksite",
        backend_url="https://writer.zhangyue.com/",
        homepage="https://www.zhangyue.com/",
        unverified=True,
        min_words=1000,
        notes=("掌阅作家专区以整本导入为主，导入前先建好作品。",) + _TXT_NOTES,
    ),
    PlatformSpec(
        key="zongheng",
        name="纵横中文网",
        group="booksite",
        backend_url="https://author.zongheng.com/",
        homepage="http://www.zongheng.com/",
        min_words=2000,
        notes=("纵横需要先建作品再上传章节，作者后台支持整本导入。",) + _TXT_NOTES,
    ),
    PlatformSpec(
        key="faloo",
        name="飞卢小说",
        group="booksite",
        backend_url="https://b.faloo.com/",
        homepage="https://b.faloo.com/",
        unverified=True,
        min_words=2000,
        notes=("飞卢偏快节奏长篇，单章普遍 2000 字以上。",) + _TXT_NOTES,
    ),
    PlatformSpec(
        key="ciweimao",
        name="刺猬猫",
        group="booksite",
        backend_url="https://www.ciweimao.com/",
        homepage="https://www.ciweimao.com/",
        unverified=True,
        batch_import=False,
        min_words=1500,
        notes=(
            "刺猬猫以二次元 / 轻小说为主，标签和封面比字数更影响曝光。",
            "没有整本导入时用「复制正文」逐章粘贴。",
        )
        + _TXT_NOTES,
    ),
    PlatformSpec(
        key="sfacg",
        name="SF轻小说",
        group="booksite",
        backend_url="https://author.sfacg.com/",
        homepage="https://book.sfacg.com/",
        unverified=True,
        min_words=1000,
        notes=(
            "SF 轻小说偏对话体与短段落，段落过长的章节建议先自行拆分。",
            "投稿前先确认作品分类（原创 / 同人），同人需要标注原作。",
        )
        + _TXT_NOTES,
    ),
    PlatformSpec(
        key="zhihu",
        name="知乎（写文章）",
        group="community",
        backend_url="https://zhuanlan.zhihu.com/write",
        homepage="https://www.zhihu.com/",
        format=FORMAT_MD,
        notes=(
            "知乎编辑器对 Markdown 支持不完整，粘贴后用「复制正文」的富文本版本更省事。",
            "连载建议在专栏里建一个「连载」文集，每章一篇。",
        ),
    ),
    PlatformSpec(
        key="douban",
        name="豆瓣阅读",
        group="community",
        backend_url="https://read.douban.com/submit/",
        homepage="https://read.douban.com/",
        format=FORMAT_MD,
        unverified=True,
        notes=("豆瓣阅读以「作品」为单位投稿，需要先在作者中心建作品再上传章节。",),
    ),
    PlatformSpec(
        key="bilibili",
        name="B站专栏",
        group="community",
        backend_url="https://member.bilibili.com/read/home",
        homepage="https://www.bilibili.com/",
        format=FORMAT_HTML,
        notes=(
            "专栏编辑器接受富文本粘贴，加粗 / 引用 / 小标题都会保留。",
            "单篇字数有上限，长篇请按章拆成多篇。",
        ),
    ),
    PlatformSpec(
        key="weibo",
        name="微博头条文章",
        group="community",
        backend_url="https://card.weibo.com/article/v3/editor",
        homepage="https://weibo.com/",
        format=FORMAT_HTML,
        notes=("头条文章是富文本编辑器，粘贴后建议再手动调一次首行缩进。",),
    ),
    PlatformSpec(
        key="lofter",
        name="Lofter",
        group="community",
        backend_url="https://www.lofter.com/newpost/",
        homepage="https://www.lofter.com/",
        format=FORMAT_HTML,
        notes=("Lofter 标签（Tag）比标题更决定曝光，发布时至少挂 3 个标签。",),
    ),
)


BY_KEY: dict[str, PlatformSpec] = {p.key: p for p in PLATFORMS}


def get(key: str) -> PlatformSpec | None:
    return BY_KEY.get(key)


def group_of(key: str) -> str:
    spec = BY_KEY.get(key)
    return spec.group if spec else ""
