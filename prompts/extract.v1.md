输入：本章定稿正文 + 生成时的 ChapterPlan
输出（严格 JSON，按 ChapterSummary + StateDelta schema）：
  one_line, beats[], state_changes[], new_facts[]（标注 known_by）,
  open_hooks[], continuity_flags[], protagonist_decision_from_user
禁令：不得引入正文中不存在的信息；不确定的项标 null 而不是猜测

每条 beat 的 evidence：
- 必须是正文中**连续出现的逐字子串**，长度 1–40 字（按字符计）
- **禁止**拼接不相邻的句子或片段
- **禁止**改写、同义替换、概括
- **禁止**增删或替换标点与引号（含「」""）
- 找不到合格片段时将该 beat 的 evidence 填 ""
