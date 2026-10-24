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

## 多版本（versions）

同一工具需要提供多个版本时（如 JDK 8 / 17 / 21），用 `versions` 代替单个安装方式，
并用 `default_version` 标注推荐版本。用户在安装前可以切换版本；每个版本各自带验证方式。

```yaml
  - name: JDK
    purpose: Java 开发工具包：编译、运行 Java 程序
    required: true
    default_version: "17"
    versions:
      - label: "8"
        winget: EclipseAdoptium.Temurin.8.JDK
        verify: { command: java -version, expect: '1\.8\.' }
      - label: "17"
        winget: Microsoft.OpenJDK.17
        verify: { command: java -version, expect: '17\.' }
    env:
      JAVA_HOME: "{install_dir}"
      path_additions: ["{install_dir}\\bin"]
```

## 环境变量（env）

工具可声明自己需要的环境变量，安装完成后由软件统一写入**用户级**环境变量
（写入前备份原值，用户可一键还原）。值里可用 `{install_dir}` 占位，
引擎会在安装后替换为该工具的实际安装路径。PATH 追加写 `path_additions` 数组。

## 安装位置

安装根目录由**用户在安装向导中选择**（软件给出默认推荐），不写死在课程包里。
引擎通过 winget 的 `--location` 参数把工具装到 `<根目录>\<工具名>`；
个别安装器不支持指定位置时，回退其默认安装路径，并在验证报告中如实标注实际位置。

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
