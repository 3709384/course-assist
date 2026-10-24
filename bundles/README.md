# 课程包规范

一门课 = 一个 YAML 文件，放在本目录下，文件名即课程 ID（如 `java-programming.yaml`），
并在 [`catalog.yaml`](catalog.yaml) 中登记。软件读取课程包完成体检、安装与验证，全程不需要改代码。

## 文件结构

```yaml
id: java-programming          # 与文件名一致，全小写连字符
name: Java 程序设计            # 课程名（与 catalog 一致）
category: programming         # 所属分类 id
summary: 一句话说明这门课的环境构成
status: draft                 # draft | community | verified（见下）
tools:                        # 需要的软件清单
  - name: OpenJDK 17
    purpose: Java 开发工具包：编译、运行 Java 程序
    winget: Microsoft.OpenJDK.17   # winget 包 ID（首选安装方式）
    version: ">=17"                # 版本要求（可选）
    required: true                 # 是否必需
    verify:                        # 验证方式：真实执行命令并检查输出
      command: java -version
      expect: '17\.'               # 对输出做正则匹配
steps: []                     # 工具之外的额外步骤（可省略）
notes: 给使用者的提醒（可省略）
```

## 安装方式（三选一，按优先级）

1. `winget: <包 ID>`：走 Windows 官方包管理器，首选。
2. `download: { url, sha256 }`：官方直链下载，**必须**提供 SHA256 校验和。
3. `manual: <说明或链接>`：无法自动安装（需授权、老师分发等）时给图文指引，软件会展示给用户照着做。

## 额外步骤 steps

```yaml
steps:
  - type: env            # 设置环境变量
    name: JAVA_HOME
    value: "%ProgramFiles%\\Microsoft\\jdk-17.0.x"
  - type: command        # 执行一条命令（如配置镜像源）
    run: pip config set global.index-url https://pypi.tuna.tsinghua.edu.cn/simple
  - type: feature        # 启用 Windows 功能（如 WSL2）
    name: wsl
  - type: manual         # 人工步骤
    title: 在 IDEA 中选择 JDK 17 作为项目 SDK
```

## 状态分级

| 状态 | 含义 |
| --- | --- |
| `draft` | 草稿：结构完整，未经实机验证 |
| `community` | 社区贡献：通过格式（Schema）校验，未做完整实机验证 |
| `verified` | 维护者实机完整走通：体检 → 安装 → 验证全部通过 |

## 校验

格式由 [`schema.json`](schema.json) 定义，提交 PR 后 CI 会自动校验；
同时建议本地先目测一遍：winget 包 ID 是否为官方源、验证命令在装完后确实能跑通。
