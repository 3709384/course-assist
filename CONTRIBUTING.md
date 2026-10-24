# 参与贡献

欢迎！这个项目最有价值的贡献不是代码，而是**课程包**——把你上过的课需要的环境整理成一个配置文件，
就能帮到所有学这门课的人。

## 贡献一个课程包（5 步）

1. 在 [`bundles/catalog.yaml`](bundles/catalog.yaml) 里找到这门课（没有就先加一行，`status: planned`）。
2. 复制一个现有课程包（如 [`bundles/java-programming.yaml`](bundles/java-programming.yaml)）改成你的课程，
   文件名用课程 ID，格式见 [`bundles/README.md`](bundles/README.md)。
3. 每个软件必须注明安装方式：winget 包 ID 优先；直链必须附 SHA256；装不了的写 `manual` 指引。
4. 每个必需软件尽量带 `verify` 验证命令，并在你自己的电脑上跑通一遍。
5. 把 catalog 里这门课的 `status` 改为 `draft`，提交 PR 并在描述里写明：课程名、你的学校/教材（可选）、实测环境（Win10/11）。

维护者实机复验通过后，状态升级为 `verified`。

## 贡献代码

- 先开 Issue 讨论再动手开发，避免重复劳动。
- 核心逻辑（解析、体检、验证）改动请附带 xUnit 测试。
- 提交信息用英文简明描述，如 `feat: add bundle parser` / `fix: handle missing winget`。

## 行为准则

友善、具体、对事不对人。新同学的第一个 PR 请多鼓励——这个项目的用户本来就是初学者。
