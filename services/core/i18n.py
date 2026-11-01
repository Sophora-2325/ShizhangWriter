"""按请求的 Accept-Language 翻译错误提示。

以后端原句为 key；{名字} 是占位，匹配时取出原句里对应的部分填进译文。
查不到的提示原样返回。AI 生成的内容不经过这里。
"""
from __future__ import annotations

import re
from typing import Any

LANGS = ("zh", "en", "ja")

# (原句, 中文, English, 日本語)
CATALOG: list[tuple[str, str, str, str]] = [
    ('上次细修请求已中断，请查看现有预览后重试', '上次细修请求已中断，请查看现有预览后重试', 'The last revision request was interrupted. Review the saved preview and retry.', '前回の修正要求が中断されました。保存済みプレビューを確認して再試行してください。'),
    ('相同请求ID内容不同', '相同请求ID内容不同', 'The request ID was reused with different content.', '同じリクエストIDに異なる内容が指定されました。'),
    ('细修请求仍在运行，请稍后重试', '细修请求仍在运行，请稍后重试', 'The revision request is still running. Retry shortly.', '修正要求は処理中です。しばらくして再試行してください。'),
    ('只读章节不可修改', '只读章节不可修改', 'Read-only chapters cannot be edited.', '閲覧専用の章は変更できません。'),
    ('细修已被其他窗口修改，请重新打开', '细修已被其他窗口修改，请重新打开', 'Another window changed the revision. Reopen it.', '別のウィンドウで修正が変更されました。開き直してください。'),
    ('正文或写作依据已改变，请刷新后重试', '正文或写作依据已改变，请刷新后重试', 'The body or writing inputs changed. Refresh and retry.', '本文または執筆資料が変更されました。更新して再試行してください。'),
    ('没有正文可细修', '没有正文可细修', 'There is no body to revise.', '修正する本文がありません。'),
    ('请先取消本章定稿，再采用修改', '请先取消本章定稿，再采用修改', 'Unfinalize the chapter before applying edits.', '修正を採用する前に章の確定を解除してください。'),
    ('所选检查问题已过期，请重新选择', '所选检查问题已过期，请重新选择', 'Selected check issues are outdated. Select again.', '選択した確認事項は古くなっています。選び直してください。'),
    ('请填写本次要求或选择检查问题', '请填写本次要求或选择检查问题', 'Enter revision instructions or select check issues.', '修正要望を入力するか確認事項を選択してください。'),
    ('请先调整规划与叠条的明确差异', '请先调整规划与叠条的明确差异', 'Resolve the explicit differences between plan and rhythm first.', '先にプランと重ね合わせの明確な相違を修正してください。'),
    ('细修输入已改变，旧结果未写入', '细修输入已改变，旧结果未写入', 'Revision inputs changed. The outdated result was not saved.', '修正の入力が変わりました。古い結果は保存されていません。'),
    ('正文或写作依据已改变，旧结果未写入', '正文或写作依据已改变，旧结果未写入', 'The body or writing inputs changed. The outdated result was not saved.', '本文または執筆資料が変わりました。古い結果は保存されていません。'),
    ('检查问题已改变', '检查问题已改变', 'The check issues changed.', '確認事項が変更されました。'),
    ('预览已过期，请重新生成', '预览已过期，请重新生成', 'The preview is outdated. Generate again.', 'プレビューが古くなっています。再生成してください。'),
    ('请选择有效修改', '请选择有效修改', 'Select valid edits.', '有効な修正を選択してください。'),
    ('包含不可采用的修改', '包含不可采用的修改', 'The selection contains unavailable edits.', '採用できない修正が含まれています。'),
    ('原文定位失效', '原文定位失效', 'The original text can no longer be located.', '原文の位置を特定できなくなりました。'),
    ('富文本修改与候选不一致，未写入', '富文本修改与候选不一致，未写入', 'The rich document does not match the selected edits. Nothing was saved.', '書式付き文書が選択した修正と一致しません。保存されていません。'),
    ('富文本无效', '富文本无效', 'The rich document is invalid.', '書式付き文書が無効です。'),
    ('必须保留原文格式', '必须保留原文格式', 'The original formatting must be preserved.', '元の書式を保持する必要があります。'),
    ('细修未完成，原文和草稿已保留，请重试', '细修未完成，原文和草稿已保留，请重试', 'Revision did not finish. The original and draft are retained. Retry.', '修正が完了しませんでした。原文と草稿は保持されています。再試行してください。'),
    ('细修未完成，原内容保留，请重试', '细修未完成，原内容保留，请重试', 'Revision did not finish. Original content is retained. Retry.', '修正が完了しませんでした。元の内容は保持されています。再試行してください。'),
    ('章节已移动或删除，请重新打开', '章节已移动或删除，请重新打开', 'The chapter moved or was deleted. Reopen it.', '章が移動または削除されました。開き直してください。'),
    ('相同请求 ID 的内容不同', '相同请求 ID 的内容不同', 'The same request ID was used with different content.', '同じリクエストIDに異なる内容が指定されました。'),
    ('同一方案请求正在处理，请稍后刷新', '同一方案请求正在处理，请稍后刷新', 'This proposal request is still running. Refresh shortly.', 'この案のリクエストは処理中です。しばらくして更新してください。'),
    ('上次方案请求被中断，请查看现有方案后用新请求重试', '上次方案请求被中断，请查看现有方案后用新请求重试', 'The previous request was interrupted. Review the saved proposal, then start a new request.', '前回のリクエストが中断されました。保存済み案を確認し、新しいリクエストで再試行してください。'),
    ('已作废章节只允许查看', '已作废章节只允许查看', 'Superseded chapters are read only.', '破棄済みの章は閲覧のみ可能です。'),
    ('方案已被其他窗口修改，请刷新后重试', '方案已被其他窗口修改，请刷新后重试', 'Another window changed this proposal. Refresh and retry.', '別のウィンドウで案が変更されました。更新して再試行してください。'),
    ('方案输入已修改，请刷新后重试', '方案输入已修改，请刷新后重试', 'Proposal inputs changed. Refresh and retry.', '案の入力資料が変更されました。更新して再試行してください。'),
    ('方案目录不可写，请恢复作品目录后重试', '方案目录不可写，请恢复作品目录后重试', 'The proposal folder is not writable. Restore access and retry.', '案のフォルダーに書き込めません。アクセスを復旧して再試行してください。'),
    ('章节已移动或删除', '章节已移动或删除', 'The chapter moved or was deleted.', '章が移動または削除されました。'),
    ('方案已被修改，本次旧结果未写入', '方案已被修改，本次旧结果未写入', 'The proposal changed. This outdated result was not saved.', '案が変更されました。古い結果は保存されていません。'),
    ('创作依据已改变，本次旧结果未写入', '创作依据已改变，本次旧结果未写入', 'Writing inputs changed. This outdated result was not saved.', '創作資料が変更されました。古い結果は保存されていません。'),
    ('方案操作未完成，原方案与作者内容保留，请重试', '方案操作未完成，原方案与作者内容保留，请重试', 'The operation did not finish. Your proposal and content are retained. Retry.', '操作が完了しませんでした。案と作者の内容は保持されています。再試行してください。'),
    ('方案操作未完成，请重试；原内容已保留', '方案操作未完成，请重试；原内容已保留', 'The operation did not finish. Retry; your content is retained.', '操作が完了しませんでした。再試行してください。元の内容は保持されています。'),
    ('请先调整本章规划与叠条的明确差异', '请先调整本章规划与叠条的明确差异', 'Resolve the explicit differences between the plan and rhythm first.', '先に章プランとリズムの明確な相違を修正してください。'),
    ('请先生成方案', '请先生成方案', 'Generate a proposal first.', '先に案を生成してください。'),
    ('答案所指问题已不存在，请刷新方案', '答案所指问题已不存在，请刷新方案', 'The question for this answer no longer exists. Refresh the proposal.', 'この回答の質問が存在しません。案を更新してください。'),
    ('方案一致性检查未完成，请重试；尚未采用', '方案一致性检查未完成，请重试；尚未采用', 'The proposal check did not finish. Retry; it has not been adopted.', '案の整合性確認が完了しませんでした。再試行してください。まだ採用されていません。'),
    ('方案存在明确矛盾，请修改后再采用', '方案存在明确矛盾，请修改后再采用', 'The proposal contains explicit contradictions. Adjust them before adoption.', '案に明確な矛盾があります。修正してから採用してください。'),
    ('没有待采用草稿', '没有待采用草稿', 'There is no draft awaiting adoption.', '採用待ちの草案がありません。'),
    ('方案历史不存在', '方案历史不存在', 'The proposal history record does not exist.', '案の履歴が存在しません。'),
    ('历史记录损坏，未改当前方案', '历史记录损坏，未改当前方案', 'The history record is damaged. The current proposal was retained.', '履歴が破損しています。現在の案は保持されています。'),
    ('当前草稿或采用稿引用的记录不能删除', '当前草稿或采用稿引用的记录不能删除', 'A record referenced by the current draft or adopted proposal cannot be deleted.', '現在の草案または採用済み案が参照する履歴は削除できません。'),
    ('正文使用的采用稿或章节身份已改变，请重新生成', '正文使用的采用稿或章节身份已改变，请重新生成', 'The adopted proposal or chapter identity used for writing changed. Generate again.', '本文で使用した採用案または章の識別情報が変わりました。再生成してください。'),
    ('章节身份已改变，请重新打开', '章节身份已改变，请重新打开', 'Chapter identity changed. Reopen the chapter.', '章の識別情報が変わりました。開き直してください。'),
    ("本章规划与叠条节奏有明确差异，请调整后重试", "本章规划与叠条节奏有明确差异，请调整后重试",
     "The chapter plan and layered rhythm disagree. Adjust them and retry.", "本章の計画と重ね合わせの展開に明確な相違があります。調整して再試行してください。"),
    ("节奏与规划的对应检查未完成，请重试；作者内容未修改", "节奏与规划的对应检查未完成，请重试；作者内容未修改",
     "The rhythm and plan check did not finish. Retry; your content is unchanged.", "展開と計画の照合が完了しませんでした。再試行してください。原文は変更していません。"),
    ("请选择已有的叠条元素", "请选择已有的叠条元素", "Choose an existing constraint item", "既存の制約項目を選んでください"),
    ("该元素仍有叠条约束，请先移除对应叠条", "该元素仍有叠条约束，请先移除对应叠条",
     "This item has layered constraints. Remove its constraint item first.", "この項目には重ね合わせ制約があります。先に対応する制約を削除してください。"),
    ("叠条走向尚未进行语义校验", "叠条走向尚未进行语义校验", "Layered directions have not been checked semantically", "重ね合わせの展開はまだ意味検証されていません"),
    ("有正文后运行本章校验，并开启语义检查", "有正文后运行本章校验，并开启语义检查",
     "Run chapter validation with semantic checking after writing the chapter", "本文を書いた後、意味検証を有効にして本章の検証を実行してください"),
    ("本章语义校验未完成，模型未返回有效结果", "本章语义校验未完成，模型未返回有效结果",
     "Semantic validation is incomplete: the model returned no valid result", "モデルから有効な結果が得られず、意味検証が完了していません"),
    ("请重试本章校验", "请重试本章校验", "Retry chapter validation", "本章の検証を再試行してください"),
    ("未知的生成方式", "未知的生成方式", "Unknown generation method", "不明な生成方式です"),
    ("请选择作品保存位置", "请选择作品保存位置", "Choose where to save the work", "作品の保存場所を選んでください"),
    ("离线模式禁用云端", "离线模式禁用云端", "Cloud models are disabled in offline mode", "オフラインモードではクラウドを使用できません"),
    ("人物不存在", "人物不存在", "Character not found", "人物が見つかりません"),
    ("个人资料必填项未完成：{fields}", "个人资料必填项未完成：{fields}",
     "Required profile fields missing: {fields}", "プロフィールの必須項目が未入力です：{fields}"),
    ("请先在第 {no} 章的评注区点「下一步」，再继续", "请先在第 {no} 章的评注区点「下一步」，再继续",
     "First click \"Next\" in the review of chapter {no}, then continue",
     "まず第 {no} 章のレビューで「次へ」を押してから続けてください"),
    ("先规划或写下本章内容", "先规划或写下本章内容", "Plan or write this chapter first", "まずこの章を計画するか書いてください"),
    ("已作废章节不可修改", "已作废章节不可修改", "Superseded chapters cannot be modified", "破棄済みの章は変更できません"),
    ("本章还没有定稿", "本章还没有定稿", "This chapter isn't finalized", "この章はまだ確定していません"),
    ("没有可导入的章节", "没有可导入的章节", "There are no chapters to import", "取り込める章がありません"),
    ("chapter not found", "章节不存在", "Chapter not found", "章が見つかりません"),
    ("project not found", "作品不存在", "Work not found", "作品が見つかりません"),
    ("无正文可定稿", "无正文可定稿", "There is no text to finalize", "確定する本文がありません"),
    ("无正文可提取摘要", "无正文可提取摘要", "There is no text to summarize", "要約を抽出する本文がありません"),
    ("阅读时长或滚动覆盖不足，请继续阅读后确认", "阅读时长或滚动覆盖不足，请继续阅读后确认",
     "Not enough reading time or scroll coverage; keep reading and confirm again",
     "閲読時間またはスクロール範囲が不足しています。読み進めてから確認してください"),
    ("unknown path {path}", "未知路径 {path}", "Unknown path {path}", "不明なパス {path}"),
    ("人物「{name}」必填项未完成：{fields}", "人物「{name}」必填项未完成：{fields}",
     "Required fields missing for character \"{name}\": {fields}", "人物「{name}」の必須項目が未入力です：{fields}"),
    ("脉络名称不能为空", "脉络名称不能为空", "The thread name cannot be empty", "筋の名前を入力してください"),
    ("只能继承本章之前、未作废章节的设定", "只能继承本章之前、未作废章节的设定",
     "You can only inherit settings from an earlier chapter that isn't superseded",
     "継承できるのは、この章より前の破棄されていない章の設定だけです"),
    ("已作废章节的设定不可修改", "已作废章节的设定不可修改", "Settings of superseded chapters cannot be modified",
     "破棄済みの章の設定は変更できません"),
    ("文件夹不存在", "文件夹不存在", "The folder does not exist", "フォルダーが存在しません"),
    ("该作品已保存在作品文件夹中", "该作品已保存在作品文件夹中", "This work is already saved in a work folder",
     "この作品はすでに作品フォルダーに保存されています"),
    ("作品名不能为空", "作品名不能为空", "The work title cannot be empty", "作品名を入力してください"),
    ("已存在同名文件夹：{path}", "已存在同名文件夹：{path}", "A folder with the same name already exists: {path}",
     "同名のフォルダーがすでに存在します：{path}"),
    ("无法重命名作品文件夹（可能有文件正被占用）：{error}", "无法重命名作品文件夹（可能有文件正被占用）：{error}",
     "Could not rename the work folder (a file may be in use): {error}",
     "作品フォルダーの名前を変更できません（ファイルが使用中の可能性があります）：{error}"),
    ("章节名不能为空", "章节名不能为空", "The chapter title cannot be empty", "章のタイトルを入力してください"),
    ("已有同名章节「{title}」", "已有同名章节「{title}」", "A chapter named \"{title}\" already exists",
     "「{title}」という章がすでにあります"),
    ("作品文件夹缺失：{path}", "作品文件夹缺失：{path}", "Work folder missing: {path}", "作品フォルダーが見つかりません：{path}"),
    ("该作品尚未迁移到作品文件夹", "该作品尚未迁移到作品文件夹", "This work hasn't been migrated to a work folder yet",
     "この作品はまだ作品フォルダーに移行されていません"),
    ("文件夹已存在且不为空：{path}", "文件夹已存在且不为空：{path}", "The folder already exists and is not empty: {path}",
     "フォルダーがすでに存在し、空ではありません：{path}"),
    ("所选文件夹里没有 {file}，不是 novel-gm 作品文件夹", "所选文件夹里没有 {file}，不是 novel-gm 作品文件夹",
     "The selected folder has no {file}; it is not a novel-gm work folder",
     "選択したフォルダーに {file} がありません。novel-gm の作品フォルダーではありません"),
    ("找不到所选图片", "找不到所选图片", "The selected image cannot be found", "選択した画像が見つかりません"),
    ("只支持 png / jpg / gif / bmp / webp 图片", "只支持 png / jpg / gif / bmp / webp 图片",
     "Only png / jpg / gif / bmp / webp images are supported", "png / jpg / gif / bmp / webp 画像のみ対応しています"),
    ("Cannot {action} while in {phase}", "当前阶段（{phase}）不能执行 {action}", "Cannot {action} while in {phase}",
     "現在の段階（{phase}）では {action} を実行できません"),
    ("Cannot transition {source} → {target}", "不能从 {source} 切换到 {target}", "Cannot transition {source} → {target}",
     "{source} から {target} へは移行できません"),
    ("你需要先读完当前章并提交细修，才能继续生成", "你需要先读完当前章并提交细修，才能继续生成",
     "You need to finish reading the current chapter and submit a refinement before generating more",
     "続けて生成するには、現在の章を読み終えて細部の修正を送信する必要があります"),
    ("你需要先读完第 {no} 章并提交细修", "你需要先读完第 {no} 章并提交细修",
     "You need to finish reading chapter {no} and submit a refinement first",
     "まず第 {no} 章を読み終えて細部の修正を送信してください"),
    # skills / plugins
    ("Skill 不存在", "Skill 不存在", "Skill not found", "スキルが見つかりません"),
    ("Skill 名称不能为空", "Skill 名称不能为空", "The skill name cannot be empty", "スキル名を入力してください"),
    ("Skill 指令不能为空", "Skill 指令不能为空", "The skill instructions cannot be empty", "スキルの指示を入力してください"),
    ("适用范围只能是 plan、generate 或 both", "适用范围只能是 plan、generate 或 both",
     "The scope must be plan, generate or both", "適用範囲は plan、generate、both のいずれかです"),
    ("找不到要导入的文件或文件夹：{path}", "找不到要导入的文件或文件夹：{path}",
     "Cannot find the file or folder to import: {path}", "インポートするファイルまたはフォルダーが見つかりません：{path}"),
    ("文件夹里没有 SKILL.md", "文件夹里没有 SKILL.md", "The folder has no SKILL.md", "フォルダーに SKILL.md がありません"),
    ("只能导入 .md 文件或含 SKILL.md 的文件夹", "只能导入 .md 文件或含 SKILL.md 的文件夹",
     "Only a .md file or a folder containing SKILL.md can be imported",
     "インポートできるのは .md ファイルか、SKILL.md を含むフォルダーだけです"),
    ("未知的戏份类型：{scene}", "未知的戏份类型：{scene}", "Unknown scene type: {scene}", "不明なシーンの種類です：{scene}"),
    ("只有内置 Skill 可以恢复为内置版本", "只有内置 Skill 可以恢复为内置版本",
     "Only built-in skills can be restored to the built-in version", "内蔵バージョンに戻せるのは内蔵スキルだけです"),
    # skill sources
    ("连接超时：{url}", "连接超时：{url}", "Connection timed out: {url}", "接続がタイムアウトしました：{url}"),
    ("无法访问 {url}：{error}", "无法访问 {url}：{error}", "Cannot open {url}: {error}", "{url} にアクセスできません：{error}"),
    ("GitHub 令牌无效，请检查后重新填写", "GitHub 令牌无效，请检查后重新填写",
     "The GitHub token is invalid; check it and enter it again", "GitHub トークンが無効です。確認して入力し直してください"),
    ("GitHub 请求次数已用完，请稍后再试，或填写 GitHub 令牌提高额度",
     "GitHub 请求次数已用完，请稍后再试，或填写 GitHub 令牌提高额度",
     "GitHub rate limit reached; try again later, or add a GitHub token for a higher limit",
     "GitHub のリクエスト上限に達しました。しばらくしてから再試行するか、GitHub トークンを入力して上限を引き上げてください"),
    ("GitHub 上找不到这个仓库或路径", "GitHub 上找不到这个仓库或路径", "That repository or path was not found on GitHub",
     "GitHub にそのリポジトリまたはパスが見つかりません"),
    ("GitHub 返回错误：HTTP {status}", "GitHub 返回错误：HTTP {status}", "GitHub returned an error: HTTP {status}",
     "GitHub がエラーを返しました：HTTP {status}"),
    ("GitHub 返回的内容无法解析", "GitHub 返回的内容无法解析", "Could not parse GitHub's response",
     "GitHub の応答を解析できませんでした"),
    ("仓库名应为 owner/name 格式", "仓库名应为 owner/name 格式", "The repository must be in owner/name form",
     "リポジトリ名は owner/name の形式で指定してください"),
    ("只支持 http 或 https 链接", "只支持 http 或 https 链接", "Only http or https links are supported",
     "http または https のリンクのみ対応しています"),
    ("找不到这个文件；如果是仓库或文件夹链接，请确认里面有 SKILL.md",
     "找不到这个文件；如果是仓库或文件夹链接，请确认里面有 SKILL.md",
     "File not found; for a repository or folder link, make sure it contains SKILL.md",
     "ファイルが見つかりません。リポジトリやフォルダーのリンクなら、SKILL.md が含まれているか確認してください"),
    ("下载失败：HTTP {status}", "下载失败：HTTP {status}", "Download failed: HTTP {status}",
     "ダウンロードに失敗しました：HTTP {status}"),
    ("这个链接是网页而不是 Markdown 文件；网页文章请用「AI 提炼」",
     "这个链接是网页而不是 Markdown 文件；网页文章请用「AI 提炼」",
     "This link is a web page, not a Markdown file; use \"Distill with AI\" for articles",
     "このリンクは Markdown ファイルではなく Web ページです。記事には「AI で抽出」を使ってください"),
    ("这个 Skill 有 {n} 字，直接导入会占用大量上下文；建议改用「AI 提炼」生成精简版",
     "这个 Skill 有 {n} 字，直接导入会占用大量上下文；建议改用「AI 提炼」生成精简版",
     "This skill is {n} characters long and would take up a lot of context; use \"Distill with AI\" to make a shorter version",
     "このスキルは {n} 文字あり、そのまま取り込むとコンテキストを大きく消費します。「AI で抽出」で短い版を作ってください"),
    ("这个网站需要登录或禁止抓取，读不到正文；请复制文章内容，改用「粘贴文本」",
     "这个网站需要登录或禁止抓取，读不到正文；请复制文章内容，改用「粘贴文本」",
     "This site requires sign-in or blocks fetching; copy the article and use \"Paste text\" instead",
     "このサイトはログインが必要か取得を禁止しています。記事をコピーして「テキストを貼り付け」を使ってください"),
    ("没能从这个网页读到足够的正文（可能需要登录或由脚本加载）；请复制文章内容，改用「粘贴文本」",
     "没能从这个网页读到足够的正文（可能需要登录或由脚本加载）；请复制文章内容，改用「粘贴文本」",
     "Couldn't read enough text from this page (it may need sign-in or load by script); copy the article and use \"Paste text\" instead",
     "このページから十分な本文を読み取れませんでした（ログインが必要か、スクリプトで読み込まれている可能性があります）。記事をコピーして「テキストを貼り付け」を使ってください"),
    ("参考内容太短，至少需要 50 个字", "参考内容太短，至少需要 50 个字",
     "The reference text is too short; at least 50 characters are needed", "参考テキストが短すぎます。50 文字以上必要です"),
    ("模型没有返回可用的 Skill，请重试或换一篇资料", "模型没有返回可用的 Skill，请重试或换一篇资料",
     "The model didn't return a usable skill; try again or use different material",
     "モデルが使えるスキルを返しませんでした。再試行するか、別の資料を使ってください"),
    ("插件不存在", "插件不存在", "Plugin not found", "プラグインが見つかりません"),
    ("无法访问系统凭据管理器，密钥未保存", "无法访问系统凭据管理器，密钥未保存",
     "Cannot access the system credential manager; the key was not saved",
     "システムの資格情報マネージャーにアクセスできないため、キーは保存されませんでした"),
    # model providers
    ("{provider} 尚未配置 API 密钥，请在「设置 → 模型」中填写", "{provider} 尚未配置 API 密钥，请在「设置 → 模型」中填写",
     "{provider} has no API key yet; add one in Settings → Models",
     "{provider} の API キーが未設定です。「設定 → モデル」で入力してください"),
    ("{provider} 尚未填写接口地址，请在「设置 → 模型」中填写", "{provider} 尚未填写接口地址，请在「设置 → 模型」中填写",
     "{provider} has no endpoint URL yet; add one in Settings → Models",
     "{provider} のエンドポイント URL が未入力です。「設定 → モデル」で入力してください"),
    ("{provider} 尚未填写模型名，请在「设置 → 模型」中填写", "{provider} 尚未填写模型名，请在「设置 → 模型」中填写",
     "{provider} has no model name yet; add one in Settings → Models",
     "{provider} のモデル名が未入力です。「設定 → モデル」で入力してください"),
    ("{provider} 拒绝了 API 密钥，请检查后重试", "{provider} 拒绝了 API 密钥，请检查后重试",
     "{provider} rejected the API key; check it and try again",
     "{provider} に API キーが拒否されました。確認してからもう一度お試しください"),
    ("无法连接 {provider}：{error}", "无法连接 {provider}：{error}", "Cannot reach {provider}: {error}",
     "{provider} に接続できません：{error}"),
    ("{provider} 调用失败：{error}", "{provider} 调用失败：{error}", "{provider} request failed: {error}",
     "{provider} の呼び出しに失敗しました：{error}"),
    ("还没有添加模型，请先在「设置 → 模型」中添加", "还没有添加模型，请先在「设置 → 模型」中添加",
     "No model has been added yet; add one in Settings → Models",
     "モデルがまだ追加されていません。「設定 → モデル」で追加してください"),
    ("离线模式需要本地模型，请先在「设置 → 模型」中添加本地模型", "离线模式需要本地模型，请先在「设置 → 模型」中添加本地模型",
     "Offline mode needs a local model; add one in Settings → Models",
     "オフラインモードにはローカルモデルが必要です。「設定 → モデル」で追加してください"),
    ("已有同名的模型配置：{name}", "已有同名的模型配置：{name}", "A model config named {name} already exists",
     "{name} という名前のモデル設定はすでにあります"),
    ("文件夹里没有 plugin.json", "文件夹里没有 plugin.json", "The folder has no plugin.json", "フォルダーに plugin.json がありません"),
    ("plugin.json 不是有效的 JSON：{error}", "plugin.json 不是有效的 JSON：{error}",
     "plugin.json is not valid JSON: {error}", "plugin.json が有効な JSON ではありません：{error}"),
    ("plugin.json 缺少必填字段：{keys}", "plugin.json 缺少必填字段：{keys}",
     "plugin.json is missing required fields: {keys}", "plugin.json に必須フィールドがありません：{keys}"),
    ("插件 id 只能包含字母、数字、点、下划线和连字符", "插件 id 只能包含字母、数字、点、下划线和连字符",
     "A plugin id may only contain letters, digits, dots, underscores and hyphens",
     "プラグイン ID に使えるのは英数字、ピリオド、アンダースコア、ハイフンだけです"),
    ("已存在同 id 的插件：{id}", "已存在同 id 的插件：{id}", "A plugin with the id {id} already exists",
     "ID が {id} のプラグインはすでに存在します"),
    ("音频为空", "音频为空", "Audio is empty", "音声が空です"),
    ("WAV 仅支持 16-bit PCM", "WAV 仅支持 16-bit PCM", "WAV must be 16-bit PCM",
     "WAV は 16-bit PCM のみ対応です"),
    ("WAV 声道数过多", "WAV 声道数过多", "Too many WAV channels", "WAV のチャンネル数が多すぎます"),
    ("语音识别失败：{error}", "语音识别失败：{error}", "Speech recognition failed: {error}",
     "音声認識に失敗しました：{error}"),
    ("未找到语音模型。请将 SenseVoice INT8 放到 %LOCALAPPDATA%\\novel-gm\\stt\\sense-voice-int8-2024-07-17，或设置 NOVEL_GM_STT_MODEL。",
     "未找到语音模型。请将 SenseVoice INT8 放到 %LOCALAPPDATA%\\novel-gm\\stt\\sense-voice-int8-2024-07-17，或设置 NOVEL_GM_STT_MODEL。",
     "Speech model not found. Place SenseVoice INT8 under %LOCALAPPDATA%\\novel-gm\\stt\\sense-voice-int8-2024-07-17, or set NOVEL_GM_STT_MODEL.",
     "音声モデルが見つかりません。SenseVoice INT8 を %LOCALAPPDATA%\\novel-gm\\stt\\sense-voice-int8-2024-07-17 に置くか、NOVEL_GM_STT_MODEL を設定してください。"),
    ("语音模型已就绪", "语音模型已就绪", "Speech model is ready", "音声モデルの準備ができました"),
]

# 人物必填项的字段名（后端报的是英文 key）
FIELD_NAMES: dict[str, tuple[str, str, str]] = {
    "name": ("姓名", "name", "名前"),
    "age": ("年龄", "age", "年齢"),
    "personality": ("性格", "personality", "性格"),
    "appearance": ("相貌", "appearance", "容姿"),
    "outfit": ("着装", "outfit", "服装"),
    "recent_thoughts": ("最近所想", "recent thoughts", "最近の考え"),
}
_FIELD_SEP = {"zh": "、", "en": ", ", "ja": "、"}


def _compile(template: str) -> re.Pattern[str]:
    parts = re.split(r"\{(\w+)\}", template)
    rx = "".join(re.escape(p) if i % 2 == 0 else f"(?P<{p}>.+?)" for i, p in enumerate(parts))
    return re.compile(rx, re.DOTALL)


_COMPILED = [(_compile(src), dict(zip(LANGS, rest))) for src, *rest in CATALOG]


def language_from_header(header: str | None) -> str:
    """Accept-Language 的第一项：zh / ja / en；没带时按中文（后端原文）。"""
    tag = (header or "").split(",")[0].strip().lower()
    if not tag:
        return "zh"
    for lang in LANGS:
        if tag.startswith(lang):
            return lang
    return "en"


def _fields(value: str, lang: str) -> str:
    idx = LANGS.index(lang)
    items = [s for s in re.split(r"[、,]\s*", value) if s]
    return _FIELD_SEP[lang].join(FIELD_NAMES[s][idx] if s in FIELD_NAMES else s for s in items)


def translate(message: str, lang: str) -> str:
    if lang not in LANGS:
        return message
    for rx, templates in _COMPILED:
        m = rx.fullmatch(message)
        if m is None:
            continue
        args = {k: _fields(v, lang) if k == "fields" else v for k, v in m.groupdict().items()}
        return templates[lang].format(**args)
    return message


def translate_detail(detail: Any, lang: str) -> Any:
    if isinstance(detail, str):
        return translate(detail, lang)
    if isinstance(detail, dict) and isinstance(detail.get("message"), str):
        return {**detail, "message": translate(detail["message"], lang)}
    return detail
